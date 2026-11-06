#nullable disable
// Agent server (docs/specs/mcp-server.md §8.3): a machine manifest, the sidecar file next to a
// machine's DLL ("Neo Poly.machine.json") that describes every parameter exactly: unit, mapping,
// choices, group and meaning. Neo machines' manifests are generated from their Params.def
// (tools/gen-machine-params.py). Other machines get drafts profiled by tools/machine-profiler.py
// (§12.2, U1), marked "status": "draft": units inferred from the machine's own value texts, the
// pitch convention and note input heard, health measured. Drafts live in Machines/Manifests and
// are copied beside their DLLs. They change nothing a song saves: every conversion is the agent's.
// U2 (docs/specs/machine-safety.md §3.1, §3.4): health says diverging, hot or needs a window, and a
// parameter can carry a safe_range, the raw range the profiler found it doesn't diverge in, which
// param_set keeps agents inside unless they pass allow_unsafe.
// U3 (§12.2, §12.9): a curated manifest ("status": "curated") is a draft a person has checked against
// the machine's source, documentation and profile: a summary that says what it's for, quirks, the
// evidence it rests on, and per parameter a plain-English name, a role (ManifestRoles) and a mapping
// that is exact (no unit read from a description alone). It lives where the draft did and replaces it.
// U6 (§12.13): a manifest generated from a machine's source ("status": "generated", tools/source_manifests.py)
// for the Pedal effects and ReBuzz's own machines: names, ranges and texts from its [ParameterDecl]s, units and
// mappings from the code that uses them, exact; it keeps the profile's measurements as a curated one does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Model
{
    public sealed class ManifestParameter
    {
        public int Index { get; init; }
        public string Id { get; init; } = "";
        public required string Name { get; init; }
        public string Group { get; init; } = "";
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        public required ParamMapping Mapping { get; init; }
        public string Description { get; init; } = "";
        /// <summary>
        /// Drafts: where the mapping came from. "values": fitted to the machine's value texts (exact to
        /// their precision); "description": read from its description ("1 ms – 4 s exponential"), approximate.
        /// Empty for generated and curated manifests (exact).
        /// </summary>
        public string Confidence { get; init; } = "";
        public bool Approximate => Confidence == "description";
        /// <summary>Where agents may set it (machine-safety.md §3.4); null: its whole range.</summary>
        public SafeRange SafeRange { get; init; }
        /// <summary>Curated: what it is in plain English ("Filter cutoff" for "VCF Freq"); param_set takes it too. Empty: none.</summary>
        public string PlainName { get; init; } = "";
        /// <summary>Curated: what it does, from <see cref="ManifestRoles.Known"/> ("filter.cutoff", "mix.wet"); empty: none.</summary>
        public string Role { get; init; } = "";
        /// <summary>
        /// Drafts (U2, mcp-server.md §12.12): the role the profiler suggests from what moving this parameter alone
        /// changes (the response's corner moves: filter.cutoff; the first echo moves: delay.time), from
        /// <see cref="ManifestRoles.Known"/>; empty: none found. Curated manifests keep their own <see cref="Role"/>.
        /// </summary>
        public string SuggestedRole { get; init; } = "";
        /// <summary>What the profiler measured behind <see cref="SuggestedRole"/> ("min → max: the −3 dB corner 120 Hz → 9.8 kHz").</summary>
        public string RoleEvidence { get; init; } = "";
        /// <summary>
        /// False when the machine's own value texts don't say what it does (Pedal Comp's Attack reads "0.1 ms" for
        /// 10 ms: a descriptor that scales the raw value): its values are read and written through the mapping only.
        /// </summary>
        public bool OwnTexts { get; init; } = true;
    }

    /// <summary>
    /// The roles a curated manifest gives its parameters (mcp-server.md §12.2, §12.9): what a parameter does,
    /// the same word on every machine, so a cutoff maps to a cutoff and a wet level to a wet level
    /// (machine_replace, U5). Dotted: area, then what.
    /// </summary>
    public static class ManifestRoles
    {
        public static readonly IReadOnlyList<string> Known = new[]
        {
            // Level, mix and stereo
            "level", "gain.input", "mix.wet", "mix.dry", "pan", "width", "bypass", "solo",
            // Pitch
            "pitch.octave", "pitch.coarse", "pitch.fine", "pitch.glide", "pitch.start", "pitch.end", "pitch.decay",
            // Oscillators and FM operators
            "osc.wave", "osc.level", "osc.mix", "osc.pulse_width", "osc.pwm", "osc.pwm_source", "osc.pwm_rate", "osc.sub", "osc.sub_wave",
            "osc.noise", "osc.detune", "osc.mode", "osc.feedback", "osc.ratio", "osc.algorithm", "osc.wave_zone", "osc.zone_split",
            // Filter
            "filter.type", "filter.slope", "filter.cutoff", "filter.resonance", "filter.bandwidth", "filter.env_amount", "filter.env_polarity",
            "filter.keytrack", "filter.velocity", "filter.drive", "filter.highpass", "filter.inertia",
            "filter.env.attack", "filter.env.decay", "filter.env.sustain", "filter.env.release", "filter.env.shape",
            // Amplitude envelope and voices
            "env.attack", "env.decay", "env.sustain", "env.release", "env.mode", "env.slope", "velocity", "velocity.amount", "accent",
            "note.length", "voices",
            // LFO
            "lfo.rate", "lfo.wave", "lfo.delay", "lfo.fade", "lfo.depth", "lfo.pitch", "lfo.filter", "lfo.amp", "lfo.pwm", "lfo.resonance",
            "lfo.env_amount", "lfo.sync", "lfo.division", "lfo.phase",
            // Delay
            "delay.time", "delay.time_left", "delay.time_right", "delay.unit", "delay.feedback", "delay.pingpong", "delay.tap", "delay.tone",
            // Reverb
            "reverb.decay", "reverb.size", "reverb.predelay", "reverb.damping", "reverb.lowcut", "reverb.early", "reverb.feedback",
            "reverb.mod_rate", "reverb.mod_depth",
            // Modulation effects
            "mod.rate", "mod.depth", "mod.delay", "mod.spread", "mod.tone", "mod.feedback",
            // Dynamics
            "dyn.threshold", "dyn.ratio", "dyn.knee", "dyn.attack", "dyn.release", "dyn.makeup", "dyn.auto_makeup", "dyn.lookahead",
            "dyn.detection", "dyn.crossover",
            // Distortion and colour
            "drive", "drive.type", "drive.threshold", "drive.clamp", "drive.tone", "drive.bass", "drive.treble", "exciter.amount",
            // EQ (band, then what)
            "eq.low.gain", "eq.low.freq", "eq.lowmid.gain", "eq.lowmid.freq", "eq.lowmid.q", "eq.mid.gain", "eq.highmid.gain",
            "eq.highmid.freq", "eq.highmid.q", "eq.high.gain", "eq.high.freq",
            // (U2 in depth: every band takes freq and q, as the profiler's suggestions name them)
            "eq.low.q", "eq.mid.freq", "eq.mid.q", "eq.high.q",
            // Drums
            "drum.level", "drum.tune", "drum.attack", "drum.decay", "drum.tone", "drum.snap", "drum.click", "drum.punch", "drum.buzz",
            "drum.decay_shape", "drum.release",
            // Groove and multi-effects
            "groove.shuffle", "groove.shuffle_step", "fx.type", "fx.amount", "fx.character", "fx.mode", "fx.env_depth", "fx.lfo_depth", "fx.chorus", "fx.limiter",
            "fx.auto_gain", "fx.morph", "fx.store", "fx.key", "fx.scale", "env.follower_release",
        };

        static readonly HashSet<string> Set = new(Known, StringComparer.Ordinal);

        public static bool IsKnown(string role) => role != null && Set.Contains(role);
    }

    /// <summary>
    /// A parameter's safe range (machine-safety.md §3.4): the raw values the profiler found it doesn't
    /// diverge in, with the others at their defaults, and why the rest is unsafe ("diverges at its
    /// maximum (100%)"). param_set refuses values outside it unless the agent passes allow_unsafe;
    /// people keep the full range in every UI.
    /// </summary>
    public sealed class SafeRange
    {
        public int Min { get; init; }
        public int Max { get; init; }
        public string Reason { get; init; } = "";
        /// <summary>The machine's texts at Min and Max when profiled (for gear_list, which has no machine to ask).</summary>
        public string MinText { get; init; } = "";
        public string MaxText { get; init; } = "";

        public bool Contains(int raw) => raw >= Min && raw <= Max;

        /// <summary>"-98.4% … 98.4%": the machine's texts when it can say them, else the profiled ones, else raw.</summary>
        public string Text(Func<int, string> describe = null) => $"{Say(Min, MinText, describe)} … {Say(Max, MaxText, describe)}";

        static string Say(int raw, string stored, Func<int, string> describe)
        {
            string text = null;
            try { text = describe?.Invoke(raw); } catch (Exception) { }
            return !string.IsNullOrWhiteSpace(text) ? text : !string.IsNullOrWhiteSpace(stored) ? stored : raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Why param_set won't set this raw value without allow_unsafe, or null when it's inside the range (or there is none).</summary>
        public static string Refusal(ParamInfo p, int raw)
        {
            var r = p?.SafeRange;
            if (r == null || r.Contains(raw)) return null;
            string value = Say(raw, "", p.Describe);
            return $"{p.Name} = {value} (raw {raw}) is outside its safe range, {r.Text(p.Describe)} (raw {r.Min}–{r.Max})" +
                   (r.Reason.Length > 0 ? $": it {r.Reason}" : "") +
                   ". Choose a value inside it, or pass allow_unsafe=true to set it anyway (then render and check the level).";
        }

        internal static SafeRange Parse(JsonObject o, int min, int max)
        {
            if (o == null) return null;
            int lo = (int?)o["min"] ?? min, hi = (int?)o["max"] ?? max;
            if (lo > hi) throw new FormatException($"A safe_range runs from {lo} to {hi}.");
            return new SafeRange
            {
                Min = lo,
                Max = hi,
                Reason = (string)o["reason"] ?? "",
                MinText = (string)o["min_text"] ?? "",
                MaxText = (string)o["max_text"] ?? "",
            };
        }
    }

    /// <summary>How a generator takes notes (drafts, §12.2): heard by the profiler.</summary>
    public sealed class ManifestNotes
    {
        /// <summary>"midi" (the engine's MIDI column), "tracks" (its Note track parameter only) or "none".</summary>
        public string Input { get; init; } = "";
        /// <summary>Semitones added to a written pitch before the machine gets it, so the written pitch plays (A4 = 440 Hz).</summary>
        public int PitchOffset { get; init; }
        /// <summary>False for unpitched generators (drums, noise): no offset applies.</summary>
        public bool Pitched { get; init; } = true;
        /// <summary>Voices heard at once (0: not measured).</summary>
        public int Polyphony { get; init; }
        /// <summary>
        /// A drum kit's keys by name (notes.map: {"Kick": 36, "Snare": "D2"}, MIDI numbers or scientific names, as
        /// the profiler reads it), in the manifest's order; empty when it names none. The machine keyboard labels them.
        /// </summary>
        public IReadOnlyList<(string Name, int Key)> Map { get; init; } = Array.Empty<(string, int)>();

        internal static IReadOnlyList<(string Name, int Key)> ParseMap(JsonNode node)
        {
            var map = new List<(string, int)>();
            if (node is not JsonObject o) return map;
            foreach (var (name, value) in o)
            {
                int key = -1;
                if (value is JsonValue v && v.TryGetValue<int>(out int n)) key = n;
                else if (value is JsonValue s && s.TryGetValue<string>(out string text) && Pitch.TryParse(text, out int parsed, out _)) key = parsed;
                if (key >= 0 && key <= 127 && !string.IsNullOrWhiteSpace(name)) map.Add((name.Trim(), key));
            }
            return map;
        }
    }

    /// <summary>
    /// What the profiler found wrong (drafts): "ok", "warn", "unhealthy" or "needs_window", with the
    /// problems. machine-safety.md §3.1: unhealthy machines that diverge (NaN, infinity, a runaway
    /// level) say so; hot ones (loud but steady) are warnings; a managed machine that can't be built
    /// without its UI needs a window, its own status.
    /// </summary>
    public sealed class ManifestHealth
    {
        public string Status { get; init; } = "ok";
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
        public bool Unhealthy => Status == "unhealthy";
        /// <summary>Its output runs away somewhere (machine_add warns): at its defaults, or at a parameter's end its safe range leaves out.</summary>
        public bool Diverging { get; init; }
        /// <summary>Loud but steady (above 0 dBFS) at some setting: a warning.</summary>
        public bool Hot { get; init; }
        public bool NeedsWindow => Status == "needs_window";
        /// <summary>Why it needs a window ("a WPF rich-text box that is the machine").</summary>
        public string Window { get; init; } = "";
    }

    /// <summary>A kind of data file a machine loads (machine_data_import): a wavetable, a DX7 bank…</summary>
    public sealed class ManifestImport
    {
        public required string Kind { get; init; }
        /// <summary>Lower-case extensions with the dot (".wav"); empty: any.</summary>
        public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
        public string Description { get; init; } = "";
    }

    public sealed class MachineManifest
    {
        public const string Format = "rebuzz-machine-manifest/1";
        public const string Suffix = ".machine.json";

        public required string Gear { get; init; }
        /// <summary>"generator" or "effect".</summary>
        public string Kind { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Source { get; init; } = "";
        public int Tracks { get; init; }
        public IReadOnlyList<ManifestParameter> Parameters { get; init; } = Array.Empty<ManifestParameter>();
        /// <summary>Track parameters described in full (drafts); generated manifests list only their names.</summary>
        public IReadOnlyList<ManifestParameter> TrackParameters { get; init; } = Array.Empty<ManifestParameter>();
        public IReadOnlyList<ManifestImport> Imports { get; init; } = Array.Empty<ManifestImport>();
        /// <summary>"draft" for a profiled manifest nobody has curated yet; empty for generated and curated ones.</summary>
        public string Status { get; init; } = "";
        public bool IsDraft => Status == "draft";
        /// <summary>What it is, in a word or two ("filter", "reverb", "synth", "drums"); drafts.</summary>
        public string Type { get; init; } = "";
        /// <summary>Note input and pitch convention (generators); null when the manifest doesn't say (Neo: MIDI, A4 = 440 Hz).</summary>
        public ManifestNotes Notes { get; init; }
        /// <summary>The profiler's health verdict; null when not profiled.</summary>
        public ManifestHealth Health { get; init; }
        /// <summary>Curated: what to know before using it ("track 0 only", "needs a wave", "stereo in, mono out").</summary>
        public IReadOnlyList<string> Quirks { get; init; } = Array.Empty<string>();
        /// <summary>Curated: what its claims rest on (its source files, documentation, the profile, its value texts).</summary>
        public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
        public bool IsCurated => Status == "curated";
        /// <summary>U6: generated from the machine's source (its parameter declarations and the code that uses them), not profiled guesswork.</summary>
        public bool IsGeneratedFromSource => Status == "generated";

        public ManifestParameter Find(string name) =>
            Parameters.FirstOrDefault(p => p.Name == name) ?? Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public ManifestParameter FindTrack(string name) =>
            TrackParameters.FirstOrDefault(p => p.Name == name) ?? TrackParameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The entry for the machine's parameter at <paramref name="index"/> in its group, named <paramref name="name"/>:
        /// by name, and among parameters that share a name (FSM Infector's " - Decay"), the one at that index.
        /// </summary>
        public ManifestParameter FindAt(bool track, int index, string name)
        {
            var list = track ? TrackParameters : Parameters;
            var named = list.Where(p => p.Name == name).ToList();
            if (named.Count == 0) named = list.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return named.Count <= 1 ? named.FirstOrDefault() : named.FirstOrDefault(p => p.Index == index);
        }

        /// <summary>"generated", "curated" or "draft": how far to trust it.</summary>
        public string Quality => IsDraft ? "draft" : Status.Length > 0 ? Status : Source.StartsWith("generated", StringComparison.OrdinalIgnoreCase) ? "generated" : "curated";

        /// <summary>The manifest beside a machine DLL ("…/Neo Poly.NET.dll" → "…/Neo Poly.machine.json"), or null.</summary>
        public static string PathFor(string dllPath, string gearName)
        {
            if (string.IsNullOrEmpty(dllPath) || string.IsNullOrEmpty(gearName)) return null;
            string dir = Path.GetDirectoryName(dllPath);
            return dir == null ? null : Path.Combine(dir, gearName + Suffix);
        }

        /// <summary>Reads a manifest file; null when there is none. A broken one throws (it's a build error, not the agent's).</summary>
        public static MachineManifest Load(string path) =>
            path != null && File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

        public static MachineManifest Parse(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A manifest is a JSON object.");
            string format = (string)root["format"];
            if (format != Format) throw new FormatException($"Unknown manifest format \"{format}\" (expected {Format}).");
            var parameters = new List<ManifestParameter>();
            if (root["parameters"] is JsonArray list)
                foreach (var node in list)
                    if (node is JsonObject p) parameters.Add(ParseParameter(p));
            // Generated manifests list track parameters by name; drafts describe them in full.
            var trackParameters = new List<ManifestParameter>();
            if (root["track_parameters"] is JsonArray tracks)
                foreach (var node in tracks)
                    if (node is JsonObject p) trackParameters.Add(ParseParameter(p));
            ManifestNotes notes = null;
            if (root["notes"] is JsonObject n)
                notes = new ManifestNotes
                {
                    Input = (string)n["input"] ?? "",
                    PitchOffset = (int?)n["pitch_offset"] ?? 0,
                    Pitched = (bool?)n["pitched"] ?? true,
                    Polyphony = (int?)n["polyphony"] ?? 0,
                    Map = ManifestNotes.ParseMap(n["map"]),
                };
            ManifestHealth health = null;
            if (root["health"] is JsonObject h)
                health = new ManifestHealth
                {
                    Status = (string)h["status"] ?? "ok",
                    Problems = (h["problems"] as JsonArray)?.Select(x => (string)x ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>(),
                    Diverging = (bool?)h["diverging"] ?? false,
                    Hot = (bool?)h["hot"] ?? false,
                    Window = (string)h["needs_window"] ?? "",
                };
            return new MachineManifest
            {
                Gear = (string)root["gear"] ?? throw new FormatException("A manifest names its gear."),
                Kind = (string)root["kind"] ?? "",
                Summary = (string)root["summary"] ?? "",
                Source = (string)root["source"] ?? "",
                Tracks = (int?)root["tracks"] ?? 0,
                Parameters = parameters,
                TrackParameters = trackParameters,
                Status = (string)root["status"] ?? "",
                Type = (string)root["type"] ?? "",
                Notes = notes,
                Health = health,
                Quirks = Strings(root["quirks"]),
                Evidence = Strings(root["evidence"]),
                Imports = (root["imports"] as JsonArray)?.OfType<JsonObject>().Select(i => new ManifestImport
                {
                    Kind = (string)i["kind"] ?? "",
                    Extensions = (i["extensions"] as JsonArray)?.Select(e => ((string)e ?? "").ToLowerInvariant()).ToList() ?? new List<string>(),
                    Description = (string)i["description"] ?? "",
                }).Where(i => i.Kind.Length > 0).ToList() ?? new List<ManifestImport>(),
            };
        }

        static IReadOnlyList<string> Strings(JsonNode node) =>
            (node as JsonArray)?.Select(x => (string)x ?? "").Where(x => x.Length > 0).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>A table point's value: a number, or "-inf"/"inf" (JSON has no infinities).</summary>
        static double PointValue(JsonNode v) =>
            v is JsonValue s && s.TryGetValue(out string text)
                ? (text.Trim().StartsWith("-") ? double.NegativeInfinity : double.PositiveInfinity)
                : (double)v!;

        /// <summary>One parameter entry as a manifest writes it (successor mappings describe parameters a manifest leaves out this way).</summary>
        internal static ManifestParameter ParseParameterEntry(JsonObject p) => ParseParameter(p);

        static ManifestParameter ParseParameter(JsonObject p)
        {
            string kind = (string)p["kind"] ?? "linear";
            var mappingKind = kind switch
            {
                "linear" => MappingKind.Linear,
                "exp" => MappingKind.Exp,
                "power" => MappingKind.Power,
                "db" => MappingKind.Db,
                "time" => MappingKind.Time,
                "freq" => MappingKind.Freq,
                "percent" => MappingKind.Percent,
                "bipolar" => MappingKind.Bipolar,
                "int" => MappingKind.Int,
                "enum" => MappingKind.Enum,
                "switch" => MappingKind.Switch,
                "ratio" => MappingKind.Ratio,
                "table" => MappingKind.Table,
                _ => throw new FormatException($"Unknown parameter kind \"{kind}\"."),
            };
            var choices = (p["choices"] as JsonArray)?.Select(c => (string)c ?? "").ToList();
            double a = (double?)p["a"] ?? 0, b = (double?)p["b"] ?? (mappingKind == MappingKind.Int ? 0 : 1);
            string unit = (string)p["unit"] ?? "";
            // A table (drafts): [raw, value] points, the machine's own values where no curve fits.
            var points = (p["points"] as JsonArray)?.OfType<JsonArray>().Where(x => x.Count >= 2)
                .Select(x => (Raw: (int)x[0]!, Value: PointValue(x[1]))).OrderBy(x => x.Raw).ToList();
            if (mappingKind == MappingKind.Table && (points == null || points.Count == 0))
                throw new FormatException($"Parameter \"{(string)p["name"]}\": a table needs points.");
            // Values in ms or kHz are kept in the base unit (s, Hz), as the agent's input is converted.
            if (mappingKind is MappingKind.Linear or MappingKind.Exp or MappingKind.Power or MappingKind.Table or MappingKind.Time or MappingKind.Freq)
            {
                double scale = unit == "ms" ? 0.001 : unit == "kHz" ? 1000 : 1;
                if (scale != 1)
                {
                    a *= scale;
                    b *= scale;
                    points = points?.Select(x => (x.Raw, x.Value * scale)).ToList();
                    unit = unit == "ms" ? "s" : "Hz";
                }
            }
            int min = (int?)p["min"] ?? 0, max = (int?)p["max"] ?? 0;
            return new ManifestParameter
            {
                Index = (int?)p["index"] ?? 0,
                Id = (string)p["id"] ?? "",
                Name = (string)p["name"] ?? throw new FormatException("A manifest parameter has a name."),
                Group = (string)p["group"] ?? "",
                Min = min,
                Max = max,
                Default = (int?)p["default"] ?? 0,
                Description = (string)p["description"] ?? "",
                Confidence = (string)p["confidence"] ?? "",
                SafeRange = SafeRange.Parse(p["safe_range"] as JsonObject, min, max),
                PlainName = (string)p["plain_name"] ?? "",
                Role = (string)p["role"] ?? "",
                SuggestedRole = (string)p["suggested_role"] ?? "",
                RoleEvidence = (string)p["role_evidence"] ?? "",
                OwnTexts = (bool?)p["own_texts"] ?? true,
                Mapping = new ParamMapping
                {
                    Kind = mappingKind,
                    A = a,
                    B = b,
                    Exponent = (double?)p["exponent"] ?? 1,
                    Flag = (bool?)p["flag"] ?? false,
                    Unit = unit,
                    Names = mappingKind == MappingKind.Switch && choices == null ? new[] { "Off", "On" } : choices,
                    Points = points,
                },
            };
        }
    }
}
