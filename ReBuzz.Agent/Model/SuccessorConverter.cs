#nullable disable
// Agent server (docs/specs/mcp-server.md §12.5, U5): what machine_replace computes before it touches
// the song. A machine's settings and its patterns' parameter columns go through a successor mapping
// (Successors.cs) in real units: old raw → the old manifest's value → the rule → the new value → the
// new manifest's raw, clamped, with a note wherever a value can't be reached or has no counterpart.
// Tracker notes become notes in the successor's pitch convention, slides become glides. Pure: the
// engine side (ReBuzz/Agent/MachineReplace.cs) reads the song and writes the results.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Guides;

namespace ReBuzz.Agent.Model
{
    /// <summary>A parameter as a conversion sees it: its range and, when known, its exact mapping.</summary>
    public sealed class SuccessorParam
    {
        public required string Name { get; init; }
        public bool Track { get; init; }
        public int Index { get; init; }
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        /// <summary>Null: raw numbers (the conversion's rule says what they mean).</summary>
        public ParamMapping Mapping { get; init; }
        /// <summary>The manifest entry (for its texts), or null.</summary>
        public ManifestParameter Entry { get; init; }

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary>A % mapping that holds numbers in percent (a linear 0 … 100 %), not fractions.</summary>
        bool PercentNumbers => Mapping != null && Mapping.Dimension == "%" && Mapping.Kind is not (MappingKind.Percent or MappingKind.Bipolar);

        public bool IsChoice => Mapping is { Kind: MappingKind.Enum or MappingKind.Switch };

        /// <summary>Hz, s, dB, % (fractions), ratio, choice, or "" (raw or unitless).</summary>
        public string Dimension => Mapping == null ? "" : IsChoice ? "choice" : Mapping.Dimension;

        public IReadOnlyList<string> Choices => Mapping?.Names;

        /// <summary>The value at a raw value in the base unit (Hz, s, dB, fractions for %; a choice's index; raw without a mapping).</summary>
        public double Value(int raw)
        {
            if (Mapping == null) return Math.Clamp(raw, Min, Max);
            double v = Mapping.Value(raw, Min, Max);
            return PercentNumbers ? v / 100 : v;
        }

        /// <summary>The raw value nearest a value in the base unit.</summary>
        public int Raw(double value)
        {
            if (double.IsNaN(value)) return Default;
            if (Mapping == null) return Math.Clamp((int)Math.Round(Math.Clamp(value, int.MinValue / 2.0, int.MaxValue / 2.0)), Min, Max);
            return Math.Clamp(Mapping.Raw(PercentNumbers ? value * 100 : value, Min, Max), Min, Max);
        }

        /// <summary>A raw value as the manual prints it ("−6.0 dB", "1.2 kHz", "Hall").</summary>
        public string Format(int raw)
        {
            if (Entry != null && Entry.Mapping != null) return MachineManual.Format(Entry, raw);
            if (Mapping != null)
                return MachineManual.Format(new ManifestParameter { Name = Name, Min = Min, Max = Max, Mapping = Mapping }, raw);
            return raw.ToString(C);
        }

        /// <summary>A value in the base unit as text, for notes ("−6.0 dB", "30 s", "150%").</summary>
        public string FormatValue(double v)
        {
            if (double.IsNegativeInfinity(v)) return Dimension == "dB" ? "-inf dB" : "-inf";
            if (double.IsPositiveInfinity(v)) return Dimension == "dB" ? "+inf dB" : "inf";
            return Dimension switch
            {
                "dB" => (v >= 0 ? "+" : "") + v.ToString("0.0", C) + " dB",
                "Hz" => v >= 1000 ? (v / 1000).ToString("0.##", C) + " kHz" : v.ToString("0.#", C) + " Hz",
                "s" => v < 1 ? (v * 1000).ToString("0.#", C) + " ms" : v.ToString("0.###", C) + " s",
                "%" => (v * 100).ToString("0.#", C) + "%",
                "choice" => Choices != null && (int)v >= 0 && (int)v < Choices.Count ? Choices[(int)v] : v.ToString("0", C),
                _ => v.ToString("0.###", C) + (Mapping?.Unit is { Length: > 0 } u ? " " + u : ""),
            };
        }

        /// <summary>For param_set-style texts ("Hall", "50%", "800 Hz").</summary>
        public ParamInfo Info => new()
        {
            Name = Name,
            Index = Index,
            Min = Min,
            Max = Max,
            Default = Default,
            Mapping = Mapping,
            Describe = Format,
        };

        public static SuccessorParam From(ManifestParameter e, bool track) => new()
        {
            Name = e.Name,
            Track = track,
            Index = e.Index,
            Min = e.Min,
            Max = e.Max,
            Default = e.Default,
            Mapping = e.Mapping,
            Entry = e,
        };

        /// <summary>A manifest's parameters, global then track.</summary>
        public static List<SuccessorParam> FromManifest(MachineManifest m) =>
            m.Parameters.Select(p => From(p, false)).Concat(m.TrackParameters.Select(p => From(p, true))).ToList();

        public SuccessorParam With(ManifestParameter described) => new()
        {
            Name = Name,
            Track = Track,
            Index = Index,
            Min = Min,
            Max = Max,
            Default = Default,
            Mapping = described.Mapping,
            Entry = new ManifestParameter
            {
                Name = Name, Index = Index, Min = Min, Max = Max, Default = Default, Mapping = described.Mapping, PlainName = described.PlainName, Role = described.Role,
            },
        };
    }

    /// <summary>A value the conversion sets on the new machine, and where it came from (the dry run's report).</summary>
    public sealed class SuccessorValue
    {
        public required SuccessorParam Param { get; init; }
        public int Raw { get; init; }
        /// <summary>"set" (the mapping's settings) or the old parameters it came from.</summary>
        public string From { get; init; } = "";
        public string FromText { get; init; } = "";
        public string Note { get; init; }

        public JsonObject Json()
        {
            var o = new JsonObject { ["param"] = Param.Name, ["value"] = Param.Format(Raw), ["raw"] = Raw, ["from"] = From };
            if (FromText.Length > 0) o["from_value"] = FromText;
            if (Note != null) o["note"] = Note;
            return o;
        }
    }

    /// <summary>The new machine's settings, the gains its connections take, its bypass.</summary>
    public sealed class SuccessorState
    {
        public List<SuccessorValue> Values { get; } = new();
        public List<string> Notes { get; } = new();
        public double InputGainDb { get; set; }
        public double OutputGainDb { get; set; }
        public bool? Bypass { get; set; }
        /// <summary>LevelsToMix: the gain kept fixed for pattern events (the output connections make up 1/k).</summary>
        public Dictionary<SuccessorRule, double> MixGain { get; } = new();

        /// <summary>The last value set for each parameter (settings first, rules after).</summary>
        public IEnumerable<(SuccessorParam Param, int Raw)> Final =>
            Values.GroupBy(v => v.Param).Select(g => (g.Key, g.Last().Raw));
    }

    /// <summary>One event of a pattern column: time in 1/240 tick, raw value, ramp length (engine columns: a ramp to the next).</summary>
    public readonly record struct ColumnEvent(int Time, int Raw, int Duration = 0);

    public sealed class SuccessorConverter
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        readonly List<SuccessorParam> olds;
        readonly List<SuccessorParam> news;

        public SuccessorMap Map { get; }
        /// <summary>Names the mapping uses that the machines don't have (a broken mapping; nothing converts).</summary>
        public List<string> Errors { get; } = new();

        public SuccessorConverter(SuccessorMap map, IEnumerable<SuccessorParam> oldParams, IEnumerable<SuccessorParam> newParams)
        {
            Map = map;
            olds = oldParams.ToList();
            news = newParams.ToList();
            // The mapping describes old parameters its manifest leaves out (non-state columns) or reads as bare numbers.
            foreach (var (name, described) in map.Describe)
            {
                int i = olds.FindIndex(p => Same(p.Name, name));
                if (i >= 0) olds[i] = olds[i].With(described);
            }
            foreach (var rule in map.Rules)
            {
                foreach (var n in rule.Inputs) if (Old(n) == null) Errors.Add($"{map.From} has no parameter \"{n}\" (rule {Kind(rule)}).");
                foreach (var n in rule.Outputs) if (New(n) == null) Errors.Add($"{map.To} has no parameter \"{n}\" (rule {Kind(rule)}).");
            }
            foreach (var (p, _) in map.Set) if (New(p) == null) Errors.Add($"{map.To} has no parameter \"{p}\" (set).");
            if (map.Notes is { } notes)
            {
                foreach (var n in new[] { notes.Note, notes.Slide, notes.SlideLength, notes.Length, notes.Volume }.Where(x => x != null))
                    if (Old(n) == null) Errors.Add($"{map.From} has no parameter \"{n}\" (notes).");
                if (notes.Glide != null && New(notes.Glide) == null) Errors.Add($"{map.To} has no parameter \"{notes.Glide}\" (notes).");
            }
        }

        static string Kind(SuccessorRule r) => r.Kind.ToString().ToLowerInvariant();

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public SuccessorParam Old(string name) => name == null ? null : olds.FirstOrDefault(p => p.Name == name) ?? olds.FirstOrDefault(p => Same(p.Name, name));

        public SuccessorParam New(string name) => name == null ? null : news.FirstOrDefault(p => p.Name == name) ?? news.FirstOrDefault(p => Same(p.Name, name));

        public IReadOnlyList<SuccessorParam> OldParams => olds;
        public IReadOnlyList<SuccessorParam> NewParams => news;

        /// <summary>Old parameters some rule or the notes read.</summary>
        public HashSet<SuccessorParam> Used
        {
            get
            {
                var set = new HashSet<SuccessorParam>(Map.Rules.SelectMany(r => r.Inputs).Select(Old).Where(p => p != null));
                if (Map.Notes is { } n)
                    foreach (var name in new[] { n.Note, n.Slide, n.SlideLength, n.Length, n.Volume })
                        if (Old(name) is { } p) set.Add(p);
                return set;
            }
        }

        // ------------------------------------------------------------------ settings

        /// <summary>The new machine's settings from the old one's (<paramref name="oldRaw"/>: a parameter's raw value; track parameters: track 0's).</summary>
        public SuccessorState ConvertState(Func<SuccessorParam, int> oldRaw)
        {
            var state = new SuccessorState { InputGainDb = Map.InputGainDb, OutputGainDb = Map.OutputGainDb };
            foreach (var (name, text) in Map.Set)
            {
                var p = New(name);
                if (p == null) continue;
                int raw;
                try { raw = ValueResolver.Resolve(p.Info, JsonValue.Create(text), p.Default); }
                catch (Exception e)
                {
                    state.Notes.Add($"{Map.Label}: setting {p.Name} to \"{text}\" failed ({e.Message}); it stays at its default.");
                    continue;
                }
                state.Values.Add(new SuccessorValue { Param = p, Raw = raw, From = "set" });
            }
            double Get(string name) => Old(name) is { } p ? p.Value(oldRaw(p)) : double.NaN;
            string Text(string name) => Old(name) is { } p ? p.Format(oldRaw(p)) : "";
            foreach (var rule in Map.Rules)
            {
                var outs = Evaluate(rule, Get, state, events: false, state.Notes);
                string from = string.Join(", ", rule.Inputs);
                string fromText = string.Join(", ", rule.Inputs.Select(Text));
                foreach (var (p, raw, note) in outs)
                    state.Values.Add(new SuccessorValue { Param = p, Raw = raw, From = from, FromText = fromText, Note = note });
            }
            return state;
        }

        // ------------------------------------------------------------------ pattern columns

        /// <summary>
        /// A pattern's parameter columns: each event of an old column some rule reads gives the new values
        /// of that rule's outputs at its time (the other inputs as they stand then: <paramref name="baseRaw"/>
        /// before the pattern's own events). Keyed by new parameter and track (0 for a global one). Ramps keep their length.
        /// </summary>
        public Dictionary<(SuccessorParam Param, int Track), List<ColumnEvent>> ConvertEvents(SuccessorState state, Func<SuccessorParam, int, int> baseRaw,
            IReadOnlyDictionary<(SuccessorParam Param, int Track), IReadOnlyList<ColumnEvent>> events, List<string> notes)
        {
            var result = new Dictionary<(SuccessorParam, int), List<ColumnEvent>>();
            var current = new Dictionary<(SuccessorParam, int), int>();
            int Now(SuccessorParam p, int track)
            {
                var key = (p, p.Track ? track : 0);
                return current.TryGetValue(key, out int v) ? v : baseRaw(p, p.Track ? track : 0);
            }
            var byInput = new Dictionary<SuccessorParam, List<SuccessorRule>>();
            foreach (var rule in Map.Rules.Where(r => r.Kind != SuccessorRuleKind.Bypass))
                foreach (var p in rule.Inputs.Select(Old).Where(p => p != null).Distinct())
                    (byInput.TryGetValue(p, out var list) ? list : byInput[p] = new List<SuccessorRule>()).Add(rule);
            var used = Used;
            foreach (var key in events.Keys.Where(k => !used.Contains(k.Param) && events[k].Count > 0).Select(k => k.Param).Distinct())
            {
                string why = Map.Unmapped.FirstOrDefault(u => Same(u.Param, key.Name)).Why;
                notes.Add($"{key.Name}'s pattern column has no counterpart on {Map.To}" + (string.IsNullOrEmpty(why) ? "." : $": {why}"));
            }
            foreach (var rule in Map.Rules.Where(r => r.Kind == SuccessorRuleKind.Bypass))
                if (events.Any(e => Same(e.Key.Param.Name, rule.From) && e.Value.Count > 0))
                    notes.Add($"{rule.From}'s pattern column can't move: it is {Map.To}'s bypass now (the machine's, not a parameter).");

            var all = events.Where(e => byInput.ContainsKey(e.Key.Param))
                .SelectMany(e => e.Value.Select(x => (e.Key.Param, e.Key.Track, Event: x)))
                .OrderBy(x => x.Event.Time).ToList();
            bool multiTrack = false;
            foreach (var group in all.GroupBy(x => x.Event.Time))
            {
                var dirty = new Dictionary<(SuccessorRule Rule, int Track), int>();
                foreach (var (p, track, e) in group)
                {
                    current[(p, p.Track ? track : 0)] = Math.Clamp(e.Raw, p.Min, p.Max);
                    foreach (var rule in byInput[p])
                    {
                        var key = (rule, p.Track ? track : 0);
                        dirty[key] = Math.Max(dirty.TryGetValue(key, out int d) ? d : 0, e.Duration);
                    }
                }
                foreach (var ((rule, track), duration) in dirty)
                {
                    double Get(string name) => Old(name) is { } p ? p.Value(Now(p, track)) : double.NaN;
                    foreach (var (p, raw, note) in Evaluate(rule, Get, state, events: true, notes))
                    {
                        if (note != null && !notes.Contains(note)) notes.Add(note);
                        int t = p.Track ? track : 0;
                        if (!p.Track && track > 0) multiTrack = true;
                        var list = result.TryGetValue((p, t), out var l) ? l : result[(p, t)] = new List<ColumnEvent>();
                        // Several tracks at one time on a global parameter: the last track's wins.
                        if (list.Count > 0 && list[^1].Time == group.Key) list[^1] = new ColumnEvent(group.Key, raw, duration);
                        else list.Add(new ColumnEvent(group.Key, raw, duration));
                    }
                }
            }
            if (multiTrack)
                notes.Add($"{Map.From}'s tracks each had their own settings; {Map.To} has one set, so every track's column writes the same parameter (the last at a time wins).");
            return result;
        }

        // ------------------------------------------------------------------ notes

        /// <summary>
        /// A pattern's tracker notes as notes for the successor: each note-on lasts to its track's next
        /// event or the pattern's end (or its length column), at the velocity its volume column gives;
        /// a slide becomes a second note that glides from it (the glide time set where it starts).
        /// <paramref name="columns"/>: the old note, slide, length and volume columns by (parameter, track).
        /// </summary>
        public List<NoteEvent> ConvertNotes(IReadOnlyDictionary<(SuccessorParam Param, int Track), IReadOnlyList<ColumnEvent>> columns, int patternTime,
            int pitchShift, double secondsPerTick, Func<SuccessorParam, int, int> baseRaw, List<(int Time, int Raw)> glides, List<string> notes)
        {
            var spec = Map.Notes;
            var result = new List<NoteEvent>();
            if (spec == null) return result;
            var noteParam = Old(spec.Note);
            if (noteParam == null) return result;
            IReadOnlyList<ColumnEvent> Column(string name, int track) =>
                Old(name) is { } p && columns.TryGetValue((p, track), out var list) ? list : Array.Empty<ColumnEvent>();
            int? At(string name, int track, int time)
            {
                foreach (var e in Column(name, track)) if (e.Time == time) return e.Raw;
                return null;
            }
            var volumeParam = Old(spec.Volume);
            var glideParam = New(spec.Glide);
            int outOfRange = 0;
            var tracks = columns.Keys.Where(k => k.Param == noteParam).Select(k => k.Track).Distinct().OrderBy(t => t).ToList();
            foreach (int track in tracks)
            {
                var events = Column(spec.Note, track).OrderBy(e => e.Time).ToList();
                // The volume a note gets: the column's last value at or before it (a state parameter holds), else the machine's.
                var volumes = volumeParam == null ? new List<ColumnEvent>() : Column(spec.Volume, track).OrderBy(v => v.Time).ToList();
                double volume = volumeParam == null || !spec.VolumeHolds ? double.NaN : volumeParam.Value(baseRaw(volumeParam, track));
                int nextVolume = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (!spec.VolumeHolds) volume = double.NaN;
                    while (nextVolume < volumes.Count && volumes[nextVolume].Time <= e.Time)
                    {
                        var v = volumes[nextVolume++];
                        if (spec.VolumeHolds || v.Time == e.Time) volume = volumeParam.Value(v.Raw);
                    }
                    int pitch = TrackNotes.FromBuzz(e.Raw);
                    if (pitch < 0) continue;
                    int end = i + 1 < events.Count ? events[i + 1].Time : patternTime;
                    if (spec.Length != null && At(spec.Length, track, e.Time) is int len && len > 0)
                        end = Math.Min(end, e.Time + len * NoteList.TimeBase);
                    end = Math.Max(end, e.Time + 1);
                    int velocity = double.IsNaN(volume) || spec.VolumeAtFull <= 0 ? spec.Velocity : Math.Clamp((int)Math.Round(127 * volume / spec.VolumeAtFull), 1, 127);
                    int sent = pitch + pitchShift;
                    if (sent < 0 || sent > 127) { outOfRange++; continue; }
                    result.Add(new NoteEvent(e.Time, end - e.Time, sent, velocity));
                    if (spec.Slide != null && At(spec.Slide, track, e.Time) is int slide && TrackNotes.FromBuzz(slide) is int to && to >= 0)
                    {
                        int target = to + pitchShift;
                        if (target < 0 || target > 127) { outOfRange++; continue; }
                        // Overlapping, so a Legato voice glides into it rather than starting again.
                        int start = Math.Min(e.Time + 1, end - 1);
                        result.Add(new NoteEvent(start, Math.Max(1, end - start), target, velocity));
                        double ticks = spec.SlideLength != null && At(spec.SlideLength, track, e.Time) is int sl && sl > 0 ? sl
                                     : spec.SlideLength != null && Old(spec.SlideLength) is { } slp ? slp.Value(baseRaw(slp, track))
                                     : (end - e.Time) / (double)NoteList.TimeBase;
                        if (glideParam != null) glides.Add((e.Time, glideParam.Raw(Math.Max(0, ticks) * secondsPerTick)));
                    }
                }
            }
            if (outOfRange > 0) notes.Add($"{outOfRange} note(s) fell outside MIDI's range after the pitch convention ({pitchShift:+0;-0} semitones) and were left out.");
            result.Sort(NoteList.Order);
            return result;
        }

        // ------------------------------------------------------------------ rules

        /// <summary>
        /// A rule's outputs (new parameter, raw, a note when the value can't be reached) from the old values.
        /// <paramref name="events"/>: a pattern event (the levels' make-up gain is fixed; connections can't follow).
        /// </summary>
        List<(SuccessorParam Param, int Raw, string Note)> Evaluate(SuccessorRule rule, Func<string, double> get, SuccessorState state, bool events, List<string> notes)
        {
            var outs = new List<(SuccessorParam, int, string)>();
            void Put(string name, double v)
            {
                var p = New(name);
                if (p == null) return;
                if (rule.Min is double lo && v < lo) v = lo;
                if (rule.Max is double hi && v > hi) v = hi;
                int raw = p.Raw(v);
                outs.Add((p, raw, Reachable(p, v, raw) ? null : $"{p.Name}: {p.FormatValue(v)} is out of its reach; set to {p.Format(raw)}."));
            }
            switch (rule.Kind)
            {
                case SuccessorRuleKind.Unit:
                    Put(rule.To, get(rule.From) * rule.Scale * (rule.Times != null ? get(rule.Times) : 1) + rule.Offset);
                    break;
                case SuccessorRuleKind.Db:
                {
                    double x = get(rule.From) * rule.Scale;
                    bool zero = rule.ZeroWhen != null && get(rule.ZeroWhen) >= 0.5;
                    Put(rule.To, zero || !(x > 0) ? double.NegativeInfinity : 20 * Math.Log10(x) + rule.Offset);
                    break;
                }
                case SuccessorRuleKind.InverseDb:
                {
                    double x = get(rule.From);
                    Put(rule.To, !(x > 0) ? double.PositiveInfinity : 20 * Math.Log10(rule.Scale / x) + rule.Offset);
                    break;
                }
                case SuccessorRuleKind.Choice:
                {
                    var from = Old(rule.From);
                    var to = New(rule.To);
                    if (from == null || to == null) break;
                    double v = get(rule.From);
                    string key = from.Choices != null && (int)v >= 0 && (int)v < from.Choices.Count ? from.Choices[(int)v] : ((int)Math.Round(v)).ToString(C);
                    string text = rule.Map.TryGetValue(key, out var mapped) ? mapped : rule.Default;
                    if (text == null)
                    {
                        string note = $"{from.Name} \"{key}\" has no counterpart on {Map.To}; {to.Name} left as it was.";
                        if (!notes.Contains(note)) notes.Add(note);
                        break;
                    }
                    try { outs.Add((to, ValueResolver.Resolve(to.Info, JsonValue.Create(text), to.Default), null)); }
                    catch (Exception e) { notes.Add($"{Map.Label}: {to.Name} = \"{text}\" failed ({e.Message})."); }
                    break;
                }
                case SuccessorRuleKind.Range:
                {
                    double x = get(rule.From), a = rule.FromRange[0], b = rule.FromRange[1], c = rule.ToRange[0], d = rule.ToRange[1];
                    double t = rule.Log && a > 0 && b > 0 && x > 0 ? Math.Log(x / a) / Math.Log(b / a) : (x - a) / (b - a);
                    t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1);
                    Put(rule.To, rule.Log && c > 0 && d > 0 ? c * Math.Pow(d / c, t) : c + t * (d - c));
                    break;
                }
                case SuccessorRuleKind.Sum:
                    Put(rule.To, rule.Terms.Sum(term => term.Weight * get(term.Param)) + rule.Offset);
                    break;
                case SuccessorRuleKind.LevelsToMix:
                    LevelsToMix(rule, get, state, events, Put, notes);
                    break;
                case SuccessorRuleKind.Clip:
                {
                    double t = get(rule.Threshold), c = get(rule.Clamp);
                    double drive = !(t > 0) ? double.PositiveInfinity : 20 * Math.Log10(1 / t) + rule.KneeDb;
                    var driveParam = New(rule.Drive);
                    if (driveParam != null)
                    {
                        double lowest = driveParam.Value(driveParam.Min);
                        // A threshold over full scale: the drive can't go below its lowest, so (once, for the settings) the input connections take the rest.
                        if (drive < lowest && !events)
                        {
                            state.InputGainDb += drive - lowest;
                            drive = lowest;
                        }
                    }
                    Put(rule.Drive, drive);
                    Put(rule.Output, !(c > 0) ? double.NegativeInfinity : 20 * Math.Log10(c) + rule.Offset);
                    break;
                }
                case SuccessorRuleKind.Bypass:
                    if (!events) state.Bypass = (get(rule.From) >= 0.5) != rule.Invert;
                    break;
                case SuccessorRuleKind.Sweep:
                    Sweep(rule, get, Put, notes);
                    break;
                case SuccessorRuleKind.DryWet:
                    DryWet(rule, get, Put, notes);
                    break;
                case SuccessorRuleKind.Graphic:
                    Graphic(rule, get, state, events, Put, notes);
                    break;
            }
            return outs;
        }

        /// <summary>A constant-Q bell's level in dB at <paramref name="w"/> times its centre: H(s) = (s² + (A/Q)s + 1) / (s² + s/(AQ) + 1), A = 10^(g/40).</summary>
        public static double BellDb(double w, double gainDb, double q)
        {
            if (gainDb == 0) return 0;
            double a = Math.Pow(10, gainDb / 40), u = (1 - w * w) * (1 - w * w);
            return 10 * Math.Log10((u + a * a * w * w / (q * q)) / (u + w * w / (a * a * q * q)));
        }

        /// <summary>
        /// The Audio EQ Cookbook's peaking filter at <paramref name="rate"/> (bandwidth <paramref name="octaves"/>, warped by
        /// ω0/sin ω0 as the cookbook does; alpha at most <paramref name="alphaMax"/>), its level in dB at <paramref name="hz"/>.
        /// </summary>
        public static double CookbookBellDb(double hz, double f0, double gainDb, double octaves, double rate, double alphaMax = double.PositiveInfinity)
        {
            if (gainDb == 0) return 0;
            double w0 = 2 * Math.PI * f0 / rate, s = Math.Sin(w0), c = Math.Cos(w0), a = Math.Pow(10, gainDb / 40);
            double alpha = Math.Min(s * Math.Sinh(Math.Log(2) / 2 * octaves * w0 / s), alphaMax);
            double w = 2 * Math.PI * hz / rate, c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double Mag2(double b0, double b1, double b2)
            {
                double re = b0 + b1 * c1 + b2 * c2, im = b1 * s1 + b2 * s2;
                return re * re + im * im;
            }
            return 10 * Math.Log10(Mag2(1 + alpha * a, -2 * c, 1 - alpha * a) / Mag2(1 + alpha / a, -2 * c, 1 - alpha / a));
        }

        /// <summary>A second-order Butterworth high-pass's level in dB at <paramref name="w"/> times its corner.</summary>
        public static double ButterworthHighpassDb(double w) => -10 * Math.Log10(1 + 1 / (w * w * w * w));

        /// <summary>
        /// The old graphic EQ's curve (its bands as bells <c>bandwidth</c> octaves wide between their half-gain points, summed
        /// in dB: constant-Q, or the cookbook's at <c>rate</c>) read at each new slider's centre. Where the bands' curve leaves
        /// the sliders' range, it moves as a whole (the least that brings it inside, or its middle to the range's when it spans
        /// more) and the output takes the move, with the old gain; a high-pass adds its cut after, unmoved. What still can't be
        /// reached is clamped, with a note. For the settings, an output gain beyond the output's reach goes on the output
        /// connections (pattern events can't move those: the output clamps).
        /// </summary>
        void Graphic(SuccessorRule rule, Func<string, double> get, SuccessorState state, bool events, Action<string, double> put, List<string> notes)
        {
            double width = get(rule.Bandwidth);
            if (double.IsNaN(width) || !(width > 0)) return;
            double q = 1 / (Math.Pow(2, width / 2) - Math.Pow(2, -width / 2));
            var gains = rule.Bands.Select(b => get(b.Param)).ToArray();
            if (gains.Any(double.IsNaN)) return;
            double Bell(double hz, SuccessorBand b, double g) =>
                rule.Rate is double rate ? CookbookBellDb(hz, b.Hz, g, width, rate, rule.AlphaMax) : BellDb(hz / b.Hz, g, q);
            var curve = rule.Sliders.Select(t => rule.Bands.Select((b, j) => Bell(t.Hz, b, gains[j])).Sum()).ToArray();
            var first = New(rule.Sliders[0].Param);
            double reachLo = first == null ? double.NegativeInfinity : Math.Min(first.Value(first.Min), first.Value(first.Max));
            double reachHi = first == null ? double.PositiveInfinity : Math.Max(first.Value(first.Min), first.Value(first.Max));
            double lo = curve.Min(), hi = curve.Max(), shift = 0;
            if (hi - lo > reachHi - reachLo)
            {
                shift = (hi + lo) / 2 - (reachHi + reachLo) / 2;
                string note = $"{Map.From}'s curve spans {(hi - lo).ToString("0.0", C)} dB, more than {Map.To}'s sliders reach ({(reachHi - reachLo).ToString("0", C)} dB): its middle is kept, its ends clamped.";
                if (!notes.Contains(note)) notes.Add(note);
            }
            else if (hi > reachHi) shift = hi - reachHi;
            else if (lo < reachLo) shift = lo - reachLo;
            double corner = rule.Highpass != null ? get(rule.Highpass) : 0;
            bool cut = false;
            for (int k = 0; k < curve.Length; k++)
            {
                double v = curve[k] - shift + (corner > 0 ? ButterworthHighpassDb(rule.Sliders[k].Hz / corner) : 0);
                if (v < reachLo - 0.05 && corner > 0 && curve[k] - shift >= reachLo - 0.05) cut = true;
                put(rule.Sliders[k].Param, Math.Clamp(v, reachLo, reachHi));
            }
            if (cut)
            {
                string note = $"{rule.Highpass} (a corner at {corner.ToString("0", C)} Hz) cuts deeper than {Map.To}'s sliders reach: the lowest stop at {reachLo.ToString("0", C)} dB.";
                if (!notes.Contains(note)) notes.Add(note);
            }
            double output = (rule.Gain != null ? get(rule.Gain) : 0) + shift;
            if (!events && double.IsFinite(output) && New(rule.Output) is { } o)
            {
                double outLo = Math.Min(o.Value(o.Min), o.Value(o.Max)), outHi = Math.Max(o.Value(o.Min), o.Value(o.Max));
                double inside = Math.Clamp(output, outLo, outHi);
                state.OutputGainDb += output - inside;
                output = inside;
            }
            put(rule.Output, output);
        }

        static string Ms(double s) => (s * 1000).ToString(s < 0.001 ? "0.##" : "0.#", C) + " ms";

        /// <summary>
        /// A delay swept between <c>lo</c> and <c>hi</c> → the new delay at the sweep's geometric centre, √(lo·hi), and its depth,
        /// log2(hi/lo)/2 octaves either side as a share of what 100 % spans. A sweep wider than the new machine reaches (or one
        /// down to no delay) keeps its top at full depth, with a note.
        /// </summary>
        void Sweep(SuccessorRule rule, Func<string, double> get, Action<string, double> put, List<string> notes)
        {
            double lo = rule.Low.Sum(t => t.Weight * get(t.Param)) + rule.LowOffset;
            double hi = rule.High.Sum(t => t.Weight * get(t.Param)) + rule.HighOffset;
            if (double.IsNaN(lo) || double.IsNaN(hi)) return;
            if (hi < lo) (lo, hi) = (hi, lo);
            double span = rule.Octaves, delay, depth;
            if (!(hi > 0)) { delay = 0; depth = 0; }
            else if (lo <= 0 || Math.Log2(hi / lo) > 2 * span)
            {
                depth = 1;
                delay = hi / Math.Pow(2, span);
                string note = $"{Map.From}'s sweep goes down to {Ms(Math.Max(0, lo))}; {Map.To}'s at full depth only to {Ms(delay / Math.Pow(2, span))} (its top, {Ms(hi)}, kept).";
                if (!notes.Contains(note)) notes.Add(note);
            }
            else
            {
                delay = Math.Sqrt(lo * hi);
                depth = Math.Log2(hi / lo) / 2 / span;
            }
            put(rule.Delay, delay);
            put(rule.Depth, depth);
        }

        /// <summary>
        /// Dry and wet amplitudes D and W (or a mix x: D = 1 − x, W = x) → mix = |W|·s / (|D| + |W|·s) and output = 20·log10(|D| + |W|·s) dB.
        /// An inverted dry or wet has no counterpart: its level is kept, its polarity is not, with a note.
        /// </summary>
        void DryWet(SuccessorRule rule, Func<string, double> get, Action<string, double> put, List<string> notes)
        {
            double d, w;
            if (rule.MixFrom != null)
            {
                double x = get(rule.MixFrom);
                d = 1 - x;
                w = x;
            }
            else
            {
                d = rule.Dry == null ? 0 : get(rule.Dry);
                w = get(rule.Wet[0].From);
            }
            if (double.IsNaN(d) || double.IsNaN(w)) return;
            string Named(string name, double v) => $"{name} at {(Old(name) is { } p ? p.FormatValue(v) : (v * 100).ToString("0.#", C) + "%")}";
            string inverted = d < 0 && w < 0 ? $"{Named(rule.Dry, d)} and {Named(rule.Wet[0].From, w)} invert the whole output (alone it sounds the same; against other paths it doesn't)"
                            : d < 0 ? $"{Named(rule.Dry, d)} inverts the dry signal against the wet, which moves the comb's notches"
                            : w < 0 ? $"{Named(rule.Wet[0].From, w)} inverts the wet signal against the dry, which moves the comb's notches"
                            : null;
            if (inverted != null)
            {
                string note = $"{inverted}; {Map.To} has no polarity for its dry or wet, so the level is kept and the polarity is not: the sound differs.";
                if (!notes.Contains(note)) notes.Add(note);
            }
            double dry = Math.Abs(d), wet = Math.Abs(w) * rule.WetScale, g = dry + wet;
            put(rule.Mix, g > 0 ? wet / g : 0);
            put(rule.Output, g > 0 ? 20 * Math.Log10(g) + rule.Offset : double.NegativeInfinity);
        }

        static double Lin(double db) => double.IsNegativeInfinity(db) || double.IsNaN(db) ? 0 : Math.Pow(10, db / 20);

        static double Db(double lin) => lin > 0 ? 20 * Math.Log10(lin) : double.NegativeInfinity;

        /// <summary>
        /// Separate dry and wet levels: out = D·in + Σ Rᵢ·wetᵢ. The successor crossfades: out = (1 − m)·in + m·Σ Lᵢ·wetᵢ,
        /// times k on its output connections. For the settings, the loudest wet level (Rᵢ times the machines' level
        /// difference) sets m = W / (D + W), with Lᵢ = Rᵢ / W and the outputs making up 1/k = D + W; with no dry, m = 1.
        /// Pattern events keep k (connections don't follow patterns): m = 1 − D·k, Lᵢ = Rᵢ·k / m.
        /// </summary>
        void LevelsToMix(SuccessorRule rule, Func<string, double> get, SuccessorState state, bool events, Action<string, double> put, List<string> notes)
        {
            double d = rule.Dry == null ? 0 : Lin(get(rule.Dry));
            var r = rule.Wet.Select(w => Lin(get(w.From)) * Lin(w.GainDb)).ToList();
            double wMax = r.Count == 0 ? 0 : r.Max();
            double k;
            var levels = new double[r.Count];
            const double MaxWet = 1.9952623149688795;   // +6 dB, the wet levels' top
            if (!events || !state.MixGain.TryGetValue(rule, out k))
            {
                // Dry only: no crossfade, the outputs carry the dry level. Wet only: all wet. Both: as above.
                k = wMax <= 0 ? (d > 0 ? 1 / d : 1) : d <= 0 ? Math.Min(1, MaxWet / wMax) : 1 / (d + wMax);
                if (!events)
                {
                    state.MixGain[rule] = k;
                    state.OutputGainDb += Db(1 / k);
                }
            }
            double m = d <= 0 ? (wMax > 0 ? 1 : 0) : Math.Clamp(1 - d * k, 0, 1);
            if (d > 0 && d * k > 1 + 1e-6)
            {
                string note = $"{rule.Dry} rises above what the crossfade can give with the output gain set for the song's settings; the dry level is capped.";
                if (!notes.Contains(note)) notes.Add(note);
            }
            for (int i = 0; i < r.Count; i++)
                levels[i] = m <= 1e-6 ? 0 : r[i] * k / m;
            if (m <= 1e-6 && wMax > 0)
            {
                string note = $"With {rule.Dry} at its level the crossfade leaves no room for the wet sound in this pattern event.";
                if (!notes.Contains(note)) notes.Add(note);
            }
            put(rule.Mix, m);
            for (int i = 0; i < r.Count; i++) put(rule.Wet[i].To, Db(levels[i]));
        }

        /// <summary>Whether the raw value lands on the value within what can be heard (0.3 dB, 3 % in frequency or time, 1 % of a level).</summary>
        static bool Reachable(SuccessorParam p, double v, int raw)
        {
            double back = p.Value(raw);
            if (double.IsInfinity(v) || double.IsInfinity(back)) return v == back || (double.IsNegativeInfinity(v) && p.Dimension == "dB" && back <= -60);
            if (double.IsNaN(v)) return true;
            return p.Dimension switch
            {
                "dB" => Math.Abs(back - v) <= 0.3,
                "Hz" or "s" => v <= 0 ? Math.Abs(back - v) < 1e-3 : Math.Abs(Math.Log(Math.Max(back, 1e-9) / v)) <= 0.03,
                "%" => Math.Abs(back - v) <= 0.011,
                "choice" => true,
                _ => p.Mapping == null ? Math.Abs(back - v) <= 0.5 : Math.Abs(back - v) <= Math.Max(0.01 * Math.Abs(v), Math.Abs(p.Value(Math.Min(p.Max, raw + 1)) - back)),
            };
        }
    }
}
