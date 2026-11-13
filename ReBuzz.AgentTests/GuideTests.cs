using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// Phase A5 (docs/specs/mcp-server.md §6): the craft guides, the machine manuals and the
    /// prompts. Everything an agent reads must be true: every tool and argument it names exists,
    /// every Neo parameter it quotes exists with a value in range, every preset it names is a
    /// factory preset, and every guide ends with a checklist the tools can check.
    /// </summary>
    [TestFixture]
    public class GuideTests
    {
        internal static readonly string[] Topics = { "workflow", "concepts", "composition", "genres", "sound-design", "synth-recipes", "fm-recipes", "mixing", "mixing-dynamics", "mastering", "mastering-chain", "dye-essence-in-depth", "dye-essence-effects", "neo-eq-in-depth", "app-control", "troubleshooting" };
        static readonly string[] NeoGear = { "Neo Analyzer", "Neo Clipper", "Neo Comp", "Neo De-esser", "Neo Delay", "Neo Drive", "Neo Dither", "Neo Drums", "Neo EQ", "Neo Ensemble", "Neo Exciter", "Neo Filter", "Neo FM6", "Neo Gate", "Neo Imager", "Neo Limiter", "Neo Low End", "Neo Multiband", "Neo Poly", "Neo Sampler", "Neo Scope", "Neo Strip", "Neo Tape", "Neo Transient", "Neo Utility", "Neo Verb", "Neo Wave", "Dye Essence", "Neo Learn Subtractive" };

        /// <summary>Neo gear being built in parallel (phase A4): parameter names aren't final, so guides give them moves in units and say to read the names with machine_describe.</summary>
        static readonly string[] PendingGear = System.Array.Empty<string>();

        /// <summary>
        /// Every tool a guide may name, with its arguments (null: not checked). Taken from the tool
        /// files, not from the spec:
        /// - main: ReBuzz/Agent/StudioTools.cs, MachineTools.cs, ParamTools.cs, PatternTools.cs,
        ///   RenderTools.cs (render with probes), AnalysisTools.cs (analyze_mix, compare, plot),
        ///   StateTools.cs (presets, snapshots), WaveTools.cs, and ReBuzz.Agent/Guides/Guides.cs (guide);
        /// - origin/agent/a3-tools: ReBuzz/Agent/CompositionTools.cs (markers, automation, session grid,
        ///   data imports, wave_from_render), RenderTools.cs (render's section), and
        ///   ReBuzz.Agent/Model/CompositionTools.cs (theory) and Transforms.cs (notes_transform).
        /// When a tool or an argument is added, renamed or removed, update this table with it.
        /// </summary>
        internal static readonly Dictionary<string, string[]> KnownTools = new()
        {
            ["studio_status"] = new string[0],
            ["guide"] = new[] { "topic", "machine" },
            ["undo"] = new string[0],
            ["redo"] = new string[0],
            ["history_list"] = new[] { "limit" },
            ["history_jump"] = new[] { "step" },
            ["song_new"] = new[] { "tempo_bpm", "discard_unsaved" },
            ["song_open"] = new[] { "path", "discard_unsaved" },
            ["song_save"] = new[] { "path", "overwrite" },
            ["song_set"] = new[] { "tempo_bpm", "ticks_per_beat", "song_end_beats", "loop_start_beats", "loop_end_beats", "looping" },
            // machine-safety.md §3.5 (ReBuzz/Agent/NotesTools.cs): Song Notes
            ["song_notes_read"] = new[] { "format" },
            ["song_notes_write"] = new[] { "text", "mode" },
            ["gear_list"] = new[] { "kind", "search", "limit", "include_unhealthy" },
            ["machine_list"] = new string[0],
            // K0 (agent-knowledge.md: ReBuzz.Agent/Guides/MachineCatalog.cs)
            ["machine_find"] = new[] { "intent", "category", "tag", "role", "kind", "limit" },
            ["machine_add"] = new[] { "gear", "name", "connect_to", "insert_after", "gain_db", "tracks" },
            ["machine_remove"] = new[] { "machines", "keep_chain" },
            ["machine_replace"] = new[] { "machine", "with", "name", "keep_old", "dry_run", "compare", "from", "to", "section" },
            ["machine_set"] = new[] { "machine", "rename", "mute", "reset", "solo", "bypass", "tracks" },
            ["connect"] = new[] { "from", "to", "gain_db", "pan", "input" },
            ["disconnect"] = new[] { "from", "to" },
            ["connection_set"] = new[] { "changes" },
            ["machine_describe"] = new[] { "machine", "group", "value_table", "table_points" },
            ["param_set"] = new[] { "machine", "changes", "allow_unsafe" },
            ["param_get"] = new[] { "machine", "params", "track" },
            ["pattern_list"] = new[] { "machine" },
            ["pattern_create"] = new[] { "machine", "name", "length_beats" },
            ["pattern_delete"] = new[] { "machine", "pattern" },
            ["pattern_rename"] = new[] { "machine", "pattern", "new_name" },
            ["pattern_duplicate"] = new[] { "machine", "pattern", "new_name" },
            ["notes_write"] = new[] { "machine", "pattern", "notes", "mode", "range_start", "range_end", "input", "apply_pitch_offset" },
            ["notes_read"] = new[] { "machine", "pattern", "range_start", "range_end", "input", "apply_pitch_offset" },
            ["arrangement_get"] = new[] { "from", "to" },
            ["arrangement_place"] = new[] { "machine", "pattern", "at", "repeat_every_beats", "times" },
            ["arrangement_clear"] = new[] { "machine", "from", "to" },
            ["render"] = new[] { "from", "to", "range", "section", "tail_seconds", "solo", "mute", "probes", "name", "bits", "dither", "cpu", "cpu_window_seconds", "opus_kbps" },
            ["analyze"] = new[] { "source", "metrics", "from_seconds", "to_seconds", "target", "opus_kbps" },
            // A2 (main: ReBuzz/Agent/AnalysisTools.cs, StateTools.cs, WaveTools.cs)
            ["analyze_mix"] = new[] { "from", "to", "section", "machines" },
            ["compare"] = new[] { "a", "b", "from_seconds", "to_seconds" },
            ["plot"] = new[] { "kind", "sources", "title", "from_seconds", "to_seconds" },
            ["preset_list"] = new[] { "machine", "search", "category", "limit", "offset" },
            ["preset_load"] = new[] { "machine", "preset" },
            ["preset_save"] = new[] { "machine", "name", "comment", "overwrite" },
            ["snapshot_save"] = new[] { "name" },
            ["snapshot_list"] = new string[0],
            ["snapshot_restore"] = new[] { "name" },
            ["snapshot_diff"] = new[] { "a", "b" },
            ["wave_list"] = new string[0],
            ["wave_import"] = new[] { "path", "slot", "name", "root_note", "replace", "layer" },
            // A3 (origin/agent/a3-tools: ReBuzz/Agent/CompositionTools.cs)
            ["arrangement_markers"] = new[] { "set", "remove" },
            ["automation_write"] = new[] { "machine", "pattern", "param", "track", "points", "shape", "clear", "allow_unsafe" },
            ["automation_read"] = new[] { "machine", "pattern", "param" },
            ["machine_data_import"] = new[] { "machine", "kind", "path", "item" },
            ["wave_from_render"] = new[] { "source", "from_seconds", "to_seconds", "slot", "name", "root_note" },
            ["session_get"] = new string[0],
            ["session_clip_set"] = new[] { "machine", "scene", "pattern", "mode", "quantization", "loop", "legato" },
            ["session_scene_set"] = new[] { "scene", "insert", "name", "tempo_bpm", "beats_per_bar" },
            // A3 (origin/agent/a3-tools: ReBuzz.Agent/Model/CompositionTools.cs, Transforms.cs)
            ["theory"] = new[] { "op", "chord", "progression", "key", "octave", "register", "voicing", "inversion", "voice_leading", "previous", "beats_per_chord", "start", "pitches", "steps", "notes" },
            ["notes_transform"] = new[] { "machine", "pattern", "transforms", "range_start", "range_end", "pitch_low", "pitch_high" },
            // A7 (ReBuzz/Agent/LiveTools.cs)
            ["transport"] = new[] { "action", "at", "loop" },
            ["session_launch"] = new[] { "scene", "machine", "pattern", "stop", "immediate", "back_to_arrangement" },
            ["session_record"] = new[] { "action", "machine", "scene", "bars" },
            ["midi_play"] = new[] { "machine", "notes", "start", "stop" },
            ["meters_read"] = new[] { "machines", "window_seconds", "clear_clip" },
            ["machine_readings"] = new[] { "machine" },
            // M1 (mastering-tools.md §6: ReBuzz/Agent/MasteringTools.cs)
            ["target_build"] = new[] { "name", "sources", "file" },
            ["eq_fit"] = new[] { "source", "target", "curve", "bands", "max_gain_db", "limit_db", "machine" },
            // K3 (agent-knowledge.md §5: ReBuzz.Agent/App/*Tools.cs)
            ["settings_get"] = new[] { "key", "section", "search", "changed_only", "limit" },
            ["settings_set"] = new[] { "key", "value" },
            ["settings_reset"] = new[] { "key", "section", "all" },
            ["settings_history"] = new[] { "key", "limit" },
            ["midi_bindings_list"] = new[] { "machine" },
            ["midi_bind"] = new[] { "machine", "parameter", "channel", "controller", "track" },
            ["midi_unbind"] = new[] { "machine", "parameter", "track", "all" },
            ["midi_learn"] = new[] { "machine", "parameter", "track", "timeout_seconds" },
            ["audio_status"] = new string[0],
            ["audio_devices"] = new string[0],
            ["audio_set"] = new[] { "device", "buffer_frames", "sample_rate" },
            ["view_show"] = new[] { "view", "machine", "pattern", "editor" },
            ["machine_open_editor"] = new[] { "machine", "window" },
            ["select"] = new[] { "machine", "pattern", "scene", "clear" },
            ["panel_set"] = new[] { "panel", "open", "size", "tab", "maximised" },
            ["window_layout"] = new[] { "width", "height", "maximised" },
            ["export_audio"] = new[] { "path", "range", "from", "to", "section", "tail_seconds", "bits", "dither", "stems", "machines", "overwrite" },
            ["app_status"] = new string[0],
            ["song_recent"] = new[] { "limit" },
        };

        /// <summary>analyze's metrics (AudioReport.Metrics on agent/a2-analysis).</summary>
        static readonly string[] Metrics = { "loudness", "peaks", "dynamics", "spectrum", "stereo", "pitch", "key", "tempo", "microdynamics", "tonal", "platform" };
        /// <summary>plot's kinds (AudioPlot.Kinds).</summary>
        static readonly string[] PlotKinds = { "spectrum", "spectrogram", "waveform", "loudness", "goniometer", "bands" };
        static readonly string[] TheoryOps = { "chord", "progression", "scale", "key", "degree", "fit" };
        static readonly string[] TransformOps = { "transpose", "quantize", "humanize", "velocity", "shift", "stretch", "reverse", "invert", "legato", "staccato" };

        /// <summary>Tool areas: a backticked snake_case word starting with one of these must be a tool.</summary>
        static readonly string[] Areas = { "studio", "song", "gear", "machine", "connection", "param", "preset", "snapshot", "pattern", "notes", "automation", "arrangement", "session", "wave", "theory", "render", "analyze", "guide", "target", "eq", "settings", "midi", "audio", "export", "view", "panel", "window" };

        // ---- the repository -------------------------------------------------------------------

        static string RepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Machines")) && Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Agent")))
                    return dir.FullName;
            Assert.Inconclusive("repository root not found");
            return "";
        }

        static Dictionary<string, MachineManifest>? manifests;
        static Dictionary<string, List<string>>? presets;

        /// <summary>The Neo manifests by gear name (Machines/*/*.machine.json, generated; not the drafts and curated manifests in Machines/Manifests).</summary>
        // The Neo machines' generated manifests, beside their sources (U6's generated from source live in Machines/Manifests).
        static Dictionary<string, MachineManifest> Manifests() =>
            manifests ??= Directory.GetFiles(Path.Combine(RepoRoot(), "Machines"), "*" + MachineManifest.Suffix, SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/Machines/Manifests/"))
                .Select(f => MachineManifest.Load(f)!).Where(m => m.Quality == "generated").ToDictionary(m => m.Gear);

        static Dictionary<string, MachineManifest>? exact;

        /// <summary>Every manifest with exact units: the Neo machines' and the curated ones (U3), by gear name.</summary>
        static Dictionary<string, MachineManifest> Exact() =>
            exact ??= Manifests().Values.Concat(CuratedManifestTests.Curated().Values).ToDictionary(m => m.Gear);

        /// <summary>
        /// A manifest's parameters by name (as a table shows it: trimmed) or, curated, by plain name; global and track
        /// (Pedal M1 has a global and a track Volume).
        /// </summary>
        static List<ManifestParameter> Named(MachineManifest m, string name)
        {
            var all = m.Parameters.Concat(m.TrackParameters).ToList();
            var found = all.Where(p => p.Name.Trim() == name.Trim()).ToList();
            return found.Count > 0 ? found : all.Where(p => p.PlainName.Length > 0 && p.PlainName == name).ToList();
        }

        /// <summary>
        /// The factory presets by gear: the Neo machines' (Machines/*/Presets.txt) and, U4 (mcp-server.md §12.11), the curated
        /// machines' (Machines/Manifests/*/&lt;Gear&gt;.presets.txt).
        /// </summary>
        static Dictionary<string, List<string>> Presets()
        {
            if (presets != null) return presets;
            presets = new Dictionary<string, List<string>>();
            var files = Directory.GetFiles(Path.Combine(RepoRoot(), "Machines"), "Presets.txt", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "Machines", "Manifests"), "*" + FactoryPresets.SourceSuffix, SearchOption.AllDirectories));
            foreach (var file in files)
            {
                string? gear = null;
                foreach (var line in File.ReadAllLines(file))
                {
                    var m = Regex.Match(line, "^machine \"(.+)\"");
                    if (m.Success) { gear = m.Groups[1].Value; presets[gear] = new List<string>(); continue; }
                    var p = Regex.Match(line, "^preset \"(.+)\"");
                    if (p.Success && gear != null) presets[gear].Add(p.Groups[1].Value);
                }
            }
            return presets;
        }

        static ParamInfo Info(ManifestParameter p) => new()
        {
            Name = p.Name, Index = p.Index, Group = p.Group, Description = p.Description,
            Min = p.Min, Max = p.Max, Default = p.Default, Mapping = p.Mapping,
            Describe = r => MachineManual.Format(p, r),
        };

        /// <summary>Every text an agent reads: the guides, the curated pages, the built manuals and the prompts (with arguments).</summary>
        static IEnumerable<(string Where, string Text, string? Gear)> Texts()
        {
            foreach (var t in Topics) yield return ($"guide {t}", GuideLibrary.Get(t)!, null);
            foreach (var g in MachineManual.CuratedGear) yield return ($"curated page {g}", MachineManual.Curated(g)!, g);
            foreach (var m in Manifests().Values) yield return ($"manual {m.Gear}", MachineManual.Build(m, Presets().GetValueOrDefault(m.Gear)), m.Gear);
            // U3: the curated manifests' manuals (their pages, if any, come with them); U4: with their factory presets.
            foreach (var m in CuratedManifestTests.Curated().Values) yield return ($"manual {m.Gear}", MachineManual.Build(m, Presets().GetValueOrDefault(m.Gear)), m.Gear);
            foreach (var (name, args) in PromptCases()) yield return ($"prompt {name}({string.Join(", ", args.Select(a => a.Key + "=" + a.Value))})", Render(name, args), null);
        }

        static IEnumerable<(string Name, Dictionary<string, string> Args)> PromptCases() => new[]
        {
            ("compose", new Dictionary<string, string> { ["genre"] = "synthwave", ["mood"] = "melancholic", ["tempo"] = "92", ["length"] = "2 minutes" }),
            ("compose", new Dictionary<string, string> { ["genre"] = "polka" }),
            ("design-sound", new Dictionary<string, string> { ["machine"] = "Neo FM6", ["description"] = "a glassy bell" }),
            ("design-sound", new Dictionary<string, string> { ["description"] = "a warm pad" }),
            ("mix", new Dictionary<string, string> { ["goals"] = "punchier kick" }),
            ("mix", new Dictionary<string, string>()),
            ("master", new Dictionary<string, string> { ["target"] = "club" }),
            ("master", new Dictionary<string, string>()),
            ("critique", new Dictionary<string, string>()),
            ("critique", new Dictionary<string, string> { ["scope"] = "Drop" }),
        };

        static string Render(string prompt, IReadOnlyDictionary<string, string> args) =>
            GuideLibrary.Prompts().Single(p => p.Name == prompt).Render(args);

        // ---- embedded ---------------------------------------------------------------------------

        [Test]
        public void EveryGuideIsEmbeddedAndEndsWithAChecklist()
        {
            Assert.That(GuideLibrary.TopicNames, Is.EquivalentTo(Topics));
            var problems = new List<string>();
            foreach (var t in Topics)
            {
                string? text = GuideLibrary.Get(t);
                if (text == null) { problems.Add($"{t}: not embedded"); continue; }
                if (!text.StartsWith("# ")) problems.Add($"{t}: no title");
                var h2 = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
                if (h2.LastOrDefault() != "Checklist") problems.Add($"{t}: the last section is \"{h2.LastOrDefault()}\", not the checklist");
                var items = (GuideLibrary.Checklist(t) ?? "").Split('\n').Where(l => l.StartsWith("- [ ]")).ToList();
                if (items.Count < 3) problems.Add($"{t}: {items.Count} checklist items");
                foreach (var item in items)
                    if (!ToolsIn(item).Any(KnownTools.ContainsKey))
                        problems.Add($"{t}: checklist item without a tool call: {item}");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            Assert.That(GuideLibrary.Resources().Select(r => r.Uri), Is.EquivalentTo(Topics.Select(t => "rebuzz://guides/" + t)));
        }

        [Test]
        public void EveryNeoMachineHasACuratedPageAndAManual()
        {
            Assert.That(MachineManual.CuratedGear, Is.EquivalentTo(NeoGear));
            Assert.That(Manifests().Keys, Is.EquivalentTo(NeoGear));
            foreach (var gear in NeoGear)
            {
                string? curated = MachineManual.Curated(gear);
                Assert.That(curated, Does.StartWith("## How it sounds"), gear);
                Assert.That(MachineManual.Curated(MachineManual.Slug(gear)), Is.EqualTo(curated), "found by its slug too");
            }
            Assert.That(MachineManual.Curated("Jeskola Bass"), Is.Null);
            Assert.That(MachineManual.Slug("Neo FM6"), Is.EqualTo("neo-fm6"));
            Assert.That(MachineManual.UriFor("Neo Poly"), Is.EqualTo("rebuzz://machines/neo-poly/manual"));
            Assert.That(MachineManual.SlugOf("rebuzz://machines/Neo%20Poly/manual"), Is.EqualTo("neo-poly"));
            Assert.That(MachineManual.SlugOf("rebuzz://guides/mixing"), Is.Null);
        }

        [Test]
        public void ManualsListEveryParameterAndPreset()
        {
            var problems = new List<string>();
            foreach (var m in Manifests().Values)
            {
                var names = Presets().GetValueOrDefault(m.Gear) ?? new List<string>();
                string text = MachineManual.Build(m, MachineManual.Curated(m.Gear), names);
                if (!text.StartsWith($"# {m.Gear} manual\n")) problems.Add($"{m.Gear}: title");
                if (!text.Contains(m.Summary)) problems.Add($"{m.Gear}: summary");
                if (!text.Contains("## How it sounds")) problems.Add($"{m.Gear}: curated page");
                if (names.Count == 0) problems.Add($"{m.Gear}: no presets found");
                foreach (var p in names)
                    if (!text.Contains("\n- " + p + "\n")) problems.Add($"{m.Gear}: preset {p} not listed");
                // Every group appears, as its own section or inside a family heading ("Operator 1 … Operator 6").
                foreach (var g in m.Parameters.Select(p => p.Group).Distinct())
                    if (!Regex.IsMatch(text, $@"^### {Regex.Escape(g)}( …|$)", RegexOptions.Multiline) && !FamilyCovers(text, g))
                        problems.Add($"{m.Gear}: group {g} missing");
                // Parameters of groups shown in full appear as table rows.
                foreach (var p in m.Parameters)
                    if (Regex.IsMatch(text, $@"^### {Regex.Escape(p.Group)}$", RegexOptions.Multiline) && !text.Contains($"| {p.Name} |"))
                        problems.Add($"{m.Gear}: {p.Name} not in its table");
                // Every value the manual prints is one param_set takes back.
                foreach (var p in m.Parameters)
                    foreach (int raw in new[] { p.Min, p.Default, p.Max }.Distinct())
                    {
                        string shown = MachineManual.Format(p, raw);
                        int back;
                        try { back = ValueResolver.Resolve(Info(p), JsonValue.Create(shown), p.Default); }
                        catch (ToolError e) { problems.Add($"{m.Gear} {p.Name}: \"{shown}\" doesn't resolve: {e.Message}"); continue; }
                        if (MachineManual.Format(p, back) != shown) problems.Add($"{m.Gear} {p.Name}: \"{shown}\" comes back as \"{MachineManual.Format(p, back)}\"");
                    }
                var resource = MachineManual.Resource(m, () => names);
                if (resource.Uri != MachineManual.UriFor(m.Gear) || resource.Read() != text) problems.Add($"{m.Gear}: resource");
            }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));

            var fm6 = MachineManual.Build(Manifests()["Neo FM6"], null, null);
            Assert.That(fm6, Does.Contain("### Operator 1 … Operator 6"), "repeated operator groups are shown once");
            Assert.That(fm6, Does.Contain("Operator 2: Op2 level 0"), "with the defaults that differ");
            Assert.That(fm6, Does.Contain("None listed"));
            var poly = MachineManual.Build(Manifests()["Neo Poly"], null, new[] { "A" });
            Assert.That(poly, Does.Contain("| F1 cutoff | 20 Hz … 20 kHz | 2.52 kHz |"));
            Assert.That(poly, Does.Contain("| Volume | -inf dB … +6.0 dB | -6.0 dB |"));
            Assert.That(poly, Does.Contain("| Voice mode | Poly · Mono · Legato | Poly |"));
        }

        static bool FamilyCovers(string text, string group)
        {
            var m = Regex.Match(group, @"^(.*?)(\d+)$");
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            foreach (Match f in Regex.Matches(text, $@"^### {Regex.Escape(m.Groups[1].Value)}(\d+) … {Regex.Escape(m.Groups[1].Value)}(\d+)$", RegexOptions.Multiline))
                if (int.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) <= n && n <= int.Parse(f.Groups[2].Value, CultureInfo.InvariantCulture)) return true;
            return false;
        }

        // ---- tools ------------------------------------------------------------------------------

        /// <summary>Tool-like names in a text: calls ("render(") and backticked area_verb words.</summary>
        static IEnumerable<string> ToolsIn(string text)
        {
            foreach (Match m in Regex.Matches(text, @"(?<![\w./-])([a-z][a-z0-9_]*)\("))
                yield return m.Groups[1].Value;
            foreach (Match m in Regex.Matches(text, @"`([a-z][a-z0-9_]*)`"))
            {
                string w = m.Groups[1].Value;
                if (KnownTools.ContainsKey(w) || (w.Contains('_') && Areas.Contains(w.Split('_')[0]))) yield return w;
            }
        }

        /// <summary>Each call in a text: the tool and its argument list's text (balanced brackets and quotes).</summary>
        static IEnumerable<(string Tool, string Args)> Calls(string text)
        {
            foreach (Match m in Regex.Matches(text, @"(?<![\w./-])([a-z][a-z0-9_]*)\("))
            {
                int depth = 0, i = m.Index + m.Length - 1;
                bool quoted = false;
                int start = i + 1;
                for (; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '"') quoted = !quoted;
                    if (quoted) continue;
                    if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}') { depth--; if (depth == 0) break; }
                    else if (c == '\n') break;
                }
                yield return (m.Groups[1].Value, text.Substring(start, Math.Max(0, Math.Min(i, text.Length) - start)));
            }
        }

        /// <summary>The top-level "name=" arguments of a call.</summary>
        static IEnumerable<string> ArgNames(string args)
        {
            int depth = 0;
            bool quoted = false;
            var piece = new StringBuilder();
            var pieces = new List<string>();
            foreach (char c in args)
            {
                if (c == '"') quoted = !quoted;
                if (!quoted)
                {
                    if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}') depth--;
                    else if (c == ',' && depth == 0) { pieces.Add(piece.ToString()); piece.Clear(); continue; }
                }
                piece.Append(c);
            }
            pieces.Add(piece.ToString());
            foreach (var p in pieces)
            {
                var m = Regex.Match(p, @"^\s*([a-z_][a-z0-9_]*)\s*=");
                if (m.Success) yield return m.Groups[1].Value;
            }
        }

        [Test]
        public void EveryToolAndArgumentTheGuidesNameExists()
        {
            var problems = new List<string>();
            foreach (var (where, text, _) in Texts())
            {
                foreach (var tool in ToolsIn(text).Distinct())
                    if (!KnownTools.ContainsKey(tool)) problems.Add($"{where}: unknown tool `{tool}`");
                foreach (var (tool, args) in Calls(text))
                {
                    if (!KnownTools.TryGetValue(tool, out var known)) continue;
                    foreach (var a in ArgNames(args))
                        if (!known.Contains(a)) problems.Add($"{where}: {tool} has no argument \"{a}\" ({tool}({args}))");
                    if (tool == "analyze")
                        foreach (Match metric in Regex.Matches(Regex.Match(args, @"metrics=\[([^\]]*)\]").Groups[1].Value, "\"([^\"]+)\""))
                            if (!Metrics.Contains(metric.Groups[1].Value)) problems.Add($"{where}: analyze has no metric \"{metric.Groups[1].Value}\"");
                    if (tool == "plot" && Regex.Match(args, "kind=\"([^\"]+)\"") is { Success: true } kind && !PlotKinds.Contains(kind.Groups[1].Value))
                        problems.Add($"{where}: plot has no kind \"{kind.Groups[1].Value}\"");
                    if (tool == "theory" && Regex.Match(args, "op=\"([^\"]+)\"") is { Success: true } op && !TheoryOps.Contains(op.Groups[1].Value))
                        problems.Add($"{where}: theory has no op \"{op.Groups[1].Value}\"");
                }
                foreach (Match op in Regex.Matches(text, "\\{\"op\": \"([^\"]+)\""))
                    if (!TransformOps.Contains(op.Groups[1].Value)) problems.Add($"{where}: notes_transform has no op \"{op.Groups[1].Value}\"");
            }
            Assert.That(problems.Distinct().ToList(), Is.Empty, string.Join("\n", problems.Distinct()));
        }

        [Test]
        public void TheToolCheckCatchesMistakes()
        {
            Assert.That(ToolsIn("Use `render_song` or mixer_set(x=1) and `notes_write`.").ToList(), Is.EquivalentTo(new[] { "mixer_set", "render_song", "notes_write" }));
            var calls = Calls("then `render(from=0, to=\"9.1\", probes=[\"Bass\", \"Kick\"], solo=[\"a, b\"])` and more").ToList();
            Assert.That(calls.Count, Is.EqualTo(1));
            Assert.That(ArgNames(calls[0].Args).ToList(), Is.EqualTo(new List<string> { "from", "to", "probes", "solo" }));
            Assert.That(ArgNames("changes=[{\"param\": \"F1 cutoff\", \"value\": \"800 Hz\"}], machine=\"Bass\"").ToList(), Is.EqualTo(new List<string> { "changes", "machine" }));
        }

        // ---- Neo parameters ---------------------------------------------------------------------

        /// <summary>
        /// Parameter names (and values) quoted in a text: the rows of tables whose first column is
        /// "Parameter", {"param": …, "value": …} changes, and param="…" arguments. Each belongs to the
        /// gear named by the nearest heading above it (or the page's gear), or to no gear.
        /// </summary>
        static IEnumerable<(string? Gear, string Name, string? Value, string Line)> QuotedParameters(string text, string? pageGear)
        {
            var stack = new List<(int Level, string? Gear)>();
            bool inParameterTable = false;
            int valueColumn = -1;   // "Value" in recipes, "Default" in manuals
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var heading = Regex.Match(line, @"^(#+)\s+(.*)$");
                if (heading.Success)
                {
                    int level = heading.Groups[1].Value.Length;
                    stack.RemoveAll(s => s.Level >= level);
                    var named = NeoGear.Concat(PendingGear).Concat(CuratedManifestTests.Curated().Keys).Where(g => heading.Groups[2].Value.Contains(g)).ToList();
                    stack.Add((level, named.Count == 1 ? named[0] : stack.LastOrDefault().Gear ?? pageGear));
                    inParameterTable = false;
                    continue;
                }
                string? gear = stack.Count > 0 ? stack[^1].Gear ?? pageGear : pageGear;
                string trimmed = line.Trim();
                if (trimmed.StartsWith("|"))
                {
                    var cells = Regex.Split(trimmed.Trim('|'), @"(?<!\\)\|").Select(c => c.Trim().Trim('`').Trim()).ToList();
                    if (cells[0] == "Parameter")
                    {
                        inParameterTable = true;
                        valueColumn = cells.FindIndex(c => c is "Value" or "Default");
                        continue;
                    }
                    if (inParameterTable && !Regex.IsMatch(cells[0], "^-+$"))
                        yield return (gear, cells[0].Replace("\\|", "|"), valueColumn > 0 && cells.Count > valueColumn && cells[valueColumn].Length > 0 ? cells[valueColumn] : null, line);
                    continue;
                }
                inParameterTable = false;
                foreach (Match m in Regex.Matches(line, "\\{\"param\": \"([^\"]+)\"(?:, \"value\": (?:\"([^\"]*)\"|(-?[0-9.]+)))?"))
                    yield return (gear, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : null, line);
                foreach (Match m in Regex.Matches(line, "param=\"([^\"]+)\""))
                    yield return (gear, m.Groups[1].Value, null, line);
            }
        }

        /// <summary>Why a value doesn't fit a parameter (null when param_set lands on it).</summary>
        static string? ValueProblem(ManifestParameter p, string value)
        {
            int raw;
            try { raw = ValueResolver.Resolve(Info(p), JsonValue.Create(value), p.Default); }
            catch (ToolError e) { return e.Message; }
            if (p.Mapping.Kind is MappingKind.Enum or MappingKind.Switch) return null;
            if (!Quantity.TryParse(value, out double v, out string unit)) return null;
            if (!Quantity.ToBase(v, unit, out double target, out string? dim)) return $"unknown unit \"{unit}\"";
            if (p.Mapping.Kind is MappingKind.Percent or MappingKind.Bipolar && (dim == "%" || dim == null)) target /= 100;
            double At(int r) => p.Mapping.Value(Math.Clamp(r, p.Min, p.Max), p.Min, p.Max);
            double back = At(raw);
            if (double.IsInfinity(target) || double.IsInfinity(back))
                return target == back ? null : $"lands on {MachineManual.Format(p, raw)}";
            double lo = Math.Min(back, Math.Min(At(raw - 1), At(raw + 1)));
            double hi = Math.Max(back, Math.Max(At(raw - 1), At(raw + 1)));
            double eps = 1e-9 * Math.Max(1, Math.Abs(target));
            return target < lo - eps || target > hi + eps ? $"outside {MachineManual.Range(p)}: it lands on {MachineManual.Format(p, raw)}" : null;
        }

        /// <summary>
        /// Every parameter name a guide, page or manual quotes is a parameter of the gear it is about (the Neo
        /// machines, and since U3 the curated ones, by name or plain name), and every value it gives lands in range.
        /// </summary>
        [Test]
        public void EveryParameterTheGuidesQuoteExistsWithAValueInRange()
        {
            var problems = new List<string>();
            int checkedNames = 0, checkedValues = 0, curatedNames = 0, curatedValues = 0;
            foreach (var (where, text, pageGear) in Texts())
                foreach (var (gear, name, value, line) in QuotedParameters(text, pageGear))
                {
                    if (gear != null && PendingGear.Contains(gear)) continue;
                    var candidates = gear != null
                        ? new[] { Exact()[gear] }.Where(m => Named(m, name).Count > 0).ToList()
                        : Manifests().Values.Where(m => Named(m, name).Count > 0).ToList();
                    checkedNames++;
                    bool curated = gear != null && Exact()[gear].IsCurated;
                    if (curated) curatedNames++;
                    if (candidates.Count == 0)
                    {
                        problems.Add($"{where}: \"{name}\" isn't a parameter of {gear ?? "any Neo machine"} ({line.Trim()})");
                        continue;
                    }
                    if (value == null) continue;
                    checkedValues++;
                    if (curated) curatedValues++;
                    var why = candidates.SelectMany(m => Named(m, name)).Select(p => ValueProblem(p, value)).ToList();
                    if (why.All(w => w != null))
                        problems.Add($"{where}: {gear ?? candidates[0].Gear} \"{name}\" = \"{value}\": {why[0]}");
                }
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
            Assert.That(checkedValues, Is.GreaterThan(300), $"{checkedNames} names, {checkedValues} values checked");
            Assert.That(curatedValues, Is.GreaterThan(400), $"{curatedNames} curated names, {curatedValues} curated values checked");
        }

        [Test]
        public void TheParameterCheckCatchesMistakes()
        {
            var poly = Manifests()["Neo Poly"].Find("F1 cutoff")!;
            Assert.That(ValueProblem(poly, "800 Hz"), Is.Null);
            Assert.That(ValueProblem(poly, "2.52 kHz"), Is.Null);
            Assert.That(ValueProblem(poly, "50 kHz"), Does.Contain("outside"));
            Assert.That(ValueProblem(poly, "-3 dB"), Is.Not.Null);
            var env = Manifests()["Neo Poly"].Find("F1 env")!;
            Assert.That(ValueProblem(env, "+45%"), Is.Null);
            Assert.That(ValueProblem(Manifests()["Neo Comp"].Find("Ratio")!, "∞:1"), Is.Null);
            Assert.That(ValueProblem(Manifests()["Neo Poly"].Find("F1 type")!, "Ladder 36"), Is.Not.Null);
            Assert.That(ValueProblem(Manifests()["Neo FM6"].Find("Op2 coarse")!, "32"), Does.Contain("outside"));
            var found = QuotedParameters("## Neo Poly\n\n| Parameter | Value | Why |\n|---|---|---|\n| F1 cutoff | 800 Hz | |\n\n## Mixing\n\n`param_set(machine=\"X\", changes=[{\"param\": \"Threshold\", \"value\": \"-20 dB\"}])`\n", null).ToList();
            Assert.That(found.Select(f => (f.Gear, f.Name, f.Value)).ToList(),
                Is.EqualTo(new List<(string?, string, string?)> { ("Neo Poly", "F1 cutoff", "800 Hz"), (null, "Threshold", "-20 dB") }));
        }

        [Test]
        public void EveryPresetTheGuidesNameExists()
        {
            var all = Presets().Values.SelectMany(p => p).ToHashSet();
            var problems = new List<string>();
            foreach (var (where, text, _) in Texts())
            {
                foreach (Match m in Regex.Matches(text, "preset=\"([^\"]+)\""))
                    if (!all.Contains(m.Groups[1].Value)) problems.Add($"{where}: no preset \"{m.Groups[1].Value}\"");
                foreach (Match m in Regex.Matches(text, "\"([A-Z][^\"\\n]* — [^\"\\n]+)\""))
                    if (!all.Contains(m.Groups[1].Value)) problems.Add($"{where}: no preset \"{m.Groups[1].Value}\"");
            }
            Assert.That(problems.Distinct().ToList(), Is.Empty, string.Join("\n", problems.Distinct()));
        }

        // ---- prompts and the guide tool ---------------------------------------------------------

        [Test]
        public void PromptsRenderWithTheirArguments()
        {
            var prompts = GuideLibrary.Prompts();
            Assert.That(prompts.Select(p => p.Name).ToList(), Is.EquivalentTo(new[] { "compose", "design-sound", "mix", "master", "critique" }));

            string compose = Render("compose", new Dictionary<string, string> { ["genre"] = "synthwave", ["mood"] = "melancholic", ["tempo"] = "92", ["length"] = "2 minutes" });
            Assert.That(compose, Does.StartWith("Compose a melancholic synthwave track in NeoBuzz Live at 92 BPM, 2 minutes long."));
            Assert.That(compose, Does.Contain("Template: \"Synthwave\""));
            Assert.That(compose, Does.Contain("48 bars of 4/4 at 92 BPM (125 s)"));
            Assert.That(compose, Does.Contain("# Composition"), "the guide comes with it");
            Assert.That(compose, Does.Contain("# Working in NeoBuzz Live"));
            string other = Render("compose", new Dictionary<string, string> { ["genre"] = "polka" });
            Assert.That(other, Does.Contain("No template for \"polka\""));
            string bars = Render("compose", new Dictionary<string, string> { ["genre"] = "house", ["length"] = "64 bars" });
            Assert.That(bars, Does.Contain("64 bars of 4/4 at 124 BPM"));

            string design = Render("design-sound", new Dictionary<string, string> { ["machine"] = "Neo FM6", ["description"] = "a glassy bell" });
            Assert.That(design, Does.Contain("\"a glassy bell\", on Neo FM6."));
            Assert.That(design, Does.Contain("# Sound design"));
            Assert.That(design, Does.Contain("rebuzz://machines/neo-fm6/manual"), "the machine's page comes with it");
            string anyMachine = Render("design-sound", new Dictionary<string, string> { ["machine"] = "Bass", ["description"] = "rounder" });
            Assert.That(anyMachine, Does.Contain("the machine \"Bass\""));

            Assert.That(Render("mix", new Dictionary<string, string> { ["goals"] = "wider pads" }), Does.Contain("with these goals: wider pads."));
            Assert.That(Render("mix", new Dictionary<string, string>()), Does.Contain("# Mixing"));

            Assert.That(Render("master", new Dictionary<string, string>()), Does.Contain("for streaming (Spotify, YouTube, Tidal, Amazon): integrated -14 LUFS ±1, true peak at or below -1 dBTP"));
            Assert.That(Render("master", new Dictionary<string, string> { ["target"] = "club" }), Does.Contain("integrated -8 LUFS ±1, true peak at or below -0.5 dBTP"));
            Assert.That(Render("master", new Dictionary<string, string> { ["target"] = "broadcast" }), Does.Contain("-23 LUFS ±0.5"));
            Assert.That(Render("master", new Dictionary<string, string> { ["target"] = "broadcast-us" }), Does.Contain("-24 LUFS ±2, true peak at or below -2 dBTP"));
            Assert.That(Render("master", new Dictionary<string, string> { ["target"] = "-12 LUFS" }), Does.Contain("integrated -12 LUFS ±0.5"));
            Assert.That(Render("master", new Dictionary<string, string>()), Does.Contain("# Mastering"));
            Assert.That(Render("master", new Dictionary<string, string> { ["reference"] = "C:/Refs/a.wav" }), Does.Contain("matching \"C:/Refs/a.wav\""));
            Assert.That(Render("master", new Dictionary<string, string>()), Does.Not.Contain("matching \""));

            string critique = Render("critique", new Dictionary<string, string>());
            Assert.That(critique, Does.Contain("read-only"));
            foreach (var t in new[] { "composition", "genres", "sound-design", "synth-recipes", "fm-recipes", "mixing", "mixing-dynamics", "mastering", "mastering-chain" })
                Assert.That(critique, Does.Contain($"guide(topic=\"{t}\")"), "every checklist comes with it");
            Assert.That(Render("critique", new Dictionary<string, string> { ["scope"] = "Drop" }), Does.Contain("render(section=\"Drop\")"));
            Assert.That(!Render("critique", new Dictionary<string, string> { ["scope"] = "mix" }).Contains("guide(topic=\"mastering\")"), "a scope picks its checklists");
        }

        [Test]
        public void EveryGenreTemplateTheComposePromptPicksIsInTheGuide()
        {
            string guide = GuideLibrary.Get("genres")!;
            foreach (var genre in new[] { "house", "techno", "dnb", "drum and bass", "synthwave", "hip-hop", "trap", "ambient", "pop", "lo-fi", "deep house" })
            {
                var m = Regex.Match(Render("compose", new Dictionary<string, string> { ["genre"] = genre }), "Template: \"([^\"]+)\"");
                Assert.That(m.Success, Is.True, genre);
                Assert.That(guide, Does.Contain("\n### " + m.Groups[1].Value + "\n"), genre);
            }
        }

        [Test]
        public async Task PromptsAreServed()
        {
            var tools = new ToolRegistry();
            GuideLibrary.Register(tools);
            var server = new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools, GuideLibrary.Prompts(), GuideLibrary.Resources());
            Task<JsonObject?> Send(JsonObject m) => server.HandleAsync(m, new McpSession(), _ => Task.CompletedTask);
            var list = await Send(TestTools.Request(1, "prompts/list"));
            var compose = list!["result"]!["prompts"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "compose")!;
            Assert.That(compose["arguments"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "genre")!["required"]!.GetValue<bool>(), Is.True);
            var get = await Send(TestTools.Request(2, "prompts/get", new JsonObject { ["name"] = "master", ["arguments"] = new JsonObject { ["target"] = "club" } }));
            Assert.That(get!["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>(), Does.Contain("club"));
            var missing = await Send(TestTools.Request(3, "prompts/get", new JsonObject { ["name"] = "design-sound", ["arguments"] = new JsonObject { ["machine"] = "Neo Poly" } }));
            Assert.That(missing!["error"]!["message"]!.GetValue<string>(), Does.Contain("needs the argument \"description\""));
        }

        [Test]
        public void TheGuideToolServesManuals()
        {
            var tools = new ToolRegistry();
            GuideLibrary.Register(tools);
            var server = new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, tools);
            var curated = TestTools.CallTool(server, "guide", new JsonObject { ["machine"] = "Neo Verb" });
            Assert.That(curated.Error, Is.False, curated.Text);
            Assert.That(curated.Data["markdown"]!.GetValue<string>(), Does.StartWith("# Neo Verb manual"));
            Assert.That(curated.Data["markdown"]!.GetValue<string>(), Does.Contain("## How it sounds"));
            var unknown = TestTools.CallTool(server, "guide", new JsonObject { ["machine"] = "Neo Kazoo" });
            Assert.That(unknown.Error, Is.True);
            Assert.That(unknown.Text, Does.Contain("Manuals: "));
            var both = TestTools.CallTool(server, "guide", new JsonObject { ["machine"] = "Neo Verb", ["topic"] = "mixing" });
            Assert.That(both.Error, Is.True);
            var list = TestTools.CallTool(server, "guide", new JsonObject());
            Assert.That(list.Data["manuals"]!.AsArray().Count, Is.EqualTo(NeoGear.Length));
            Assert.That(TestTools.CallTool(server, "guide", new JsonObject { ["topic"] = "mastering" }).Data["markdown"]!.GetValue<string>(), Does.StartWith("# Mastering"));

            // With the ReBuzz side's provider, the full manual (manifest and presets) is served.
            var full = new ToolRegistry();
            var verb = Manifests()["Neo Verb"];
            GuideLibrary.Register(full, gear => MachineManual.Slug(gear) == "neo-verb" ? MachineManual.Build(verb, Presets()["Neo Verb"]) : null);
            var withManifest = TestTools.CallTool(new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, full), "guide", new JsonObject { ["machine"] = "neo verb" });
            Assert.That(withManifest.Data["markdown"]!.GetValue<string>(), Does.Contain("| Pre-delay | 0 ms … 250 ms |"));
            Assert.That(withManifest.Data["markdown"]!.GetValue<string>(), Does.Contain("- Plate — Snare"));

            // U3: every installed gear with a manifest has a manual, drafts and curated ones too, and the list says so.
            var gear = new ToolRegistry();
            var profiled = CuratedManifestTests.Profiled();
            GuideLibrary.Register(gear, g => profiled.Values.FirstOrDefault(m => MachineManual.Slug(m.Gear) == MachineManual.Slug(g)) is { } m ? MachineManual.Build(m, null) : null,
                () => profiled.Keys.ToList());
            var server2 = new McpServer(new ServerInfo { Name = "t", Title = "t", Version = "1" }, gear);
            var listed = TestTools.CallTool(server2, "guide", new JsonObject()).Data["manuals"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert.That(listed, Is.SupersetOf(profiled.Keys).And.SupersetOf(NeoGear));
            var draft = TestTools.CallTool(server2, "guide", new JsonObject { ["machine"] = "jeskola organ" });   // U7 made Noise Generator's from its source
            Assert.That(draft.Error, Is.False, draft.Text);
            Assert.That(draft.Data["markdown"]!.GetValue<string>(), Does.StartWith("# Jeskola Organ manual").And.Contain("profiled draft"));
            var sh101 = TestTools.CallTool(server2, "guide", new JsonObject { ["machine"] = "Pedal SH101" }).Data["markdown"]!.GetValue<string>();
            Assert.That(sh101, Does.Contain("**Filter cutoff** (`filter.cutoff`)").And.Contain("## Quirks"));
            var typo = TestTools.CallTool(server2, "guide", new JsonObject { ["machine"] = "Pedal SH-1O1" });
            Assert.That(typo.Error, Is.True);
            Assert.That(typo.Text, Does.Contain("Did you mean \"Pedal SH101\""));
        }
    }
}
