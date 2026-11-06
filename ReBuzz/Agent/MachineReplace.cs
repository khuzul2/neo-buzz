// Fork (docs/specs/mcp-server.md §12.5, §12.7 U5; machine-safety.md §4): machine_replace's engine
// side, shared by the tool and the machine menu's Replace (the studio and ReBuzz.exe). A successor
// mapping (ReBuzz.Agent/Model/Successors.cs) moves a machine onto its Neo successor:
//  - the successor goes in its place: the same connections in and out (gains and pans, plus the
//    mapping's input and output gain), its position, its name (or a given one), mute and bypass;
//  - every parameter through real units (SuccessorConverter), clamped with a note where a value
//    can't be reached or has no counterpart;
//  - its patterns: each becomes a pattern of the successor with the same name and length, whose
//    engine-played columns hold the converted parameter columns (from the engine's automation
//    columns and the pattern editor's tracker columns alike; ramps kept) and the notes (tracker
//    notes as notes in the successor's pitch convention, slides as glides; MIDI notes as they are);
//  - the arrangement: the successor's sequences beside the old ones with the same events, and the
//    Session View's tracks and clip settings (SessionLayout.Adopt);
//  - then the old machine is deleted (or kept, muted), and it all is one undo step (the caller's group).
// Before and after, the song (or a part) is rendered and compared, the machine's own output (a probe
// on its first output) against the pair's tolerance. A dry run reports the mapping and changes nothing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Agent
{
    internal sealed class ReplaceOptions
    {
        /// <summary>The successor's name; default: the old machine's (a free name when the old one stays).</summary>
        public string Name { get; init; }
        /// <summary>Keep the old machine in the song, muted (its connections and patterns as they are), instead of deleting it.</summary>
        public bool KeepOld { get; init; }
        /// <summary>Report the mapping and change nothing.</summary>
        public bool DryRun { get; init; }
        /// <summary>Render before and after and compare.</summary>
        public bool Compare { get; init; } = true;
        /// <summary>The part compared, in ticks (default: the song from its start).</summary>
        public int? From { get; init; }
        public int? To { get; init; }
        public double TailSeconds { get; init; } = 2;
        /// <summary>Without a range, the comparison covers at most this much of the song.</summary>
        public double MaxSeconds { get; init; } = 60;
    }

    internal static class MachineReplace
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static IMachineDLL Dll(ReBuzzCore core, string name) =>
            core.MachineDLLs.Values.FirstOrDefault(d => d.Name == name) ?? core.MachineDLLs.Values.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The mappings from a gear to successors this ReBuzz has installed.</summary>
        public static List<SuccessorMap> Successors(ReBuzzCore core, IMachineDLL dll) =>
            dll == null ? new List<SuccessorMap>() :
            SuccessorLibrary.For(dll.Name).Where(m => Dll(core, m.To) is { IsMissing: false, IsCrashed: false } d && SongModel.Manifest(d) != null).ToList();

        /// <summary>"successor: Neo Verb (machine_replace)" lines for gear_list and machine_describe; null when there's none.</summary>
        public static JsonArray SuccessorsJson(ReBuzzCore core, IMachineDLL dll)
        {
            var list = Successors(core, dll);
            if (list.Count == 0) return null;
            return new JsonArray(list.Select(m => (JsonNode)new JsonObject
            {
                ["gear"] = m.To,
                ["how"] = $"machine_replace(machine=…, with=\"{m.To}\")",
                ["summary"] = m.Summary,
            }).ToArray());
        }

        sealed class Plan
        {
            public MachineCore Old;
            public IMachineDLL To;
            public SuccessorMap Map;
            public SuccessorConverter Converter;
            public SuccessorState State;
            public int OldPitchOffset;
        }

        static Plan MakePlan(ReBuzzCore core, MachineCore old, string toGear)
        {
            if (SongModel.IsMaster(old)) throw new ToolError("Master can't be replaced.");
            var maps = SuccessorLibrary.For(old.DLL?.Name).ToList();
            if (maps.Count == 0)
                throw new ToolError($"{old.Name} ({old.DLL?.Name}) has no successor mapping. Gear with one: " +
                                    string.Join(", ", SuccessorLibrary.All.Select(m => m.From).Distinct().OrderBy(x => x)) + ".");
            SuccessorMap map;
            if (string.IsNullOrWhiteSpace(toGear))
            {
                var installed = Successors(core, old.DLL);
                if (installed.Count == 0) throw new ToolError($"None of {old.DLL.Name}'s successors ({string.Join(", ", maps.Select(m => m.To))}) is installed.");
                if (installed.Count > 1) throw new ToolError($"{old.DLL.Name} has several successors: {string.Join(", ", installed.Select(m => m.To))}. Choose one with: with.");
                map = installed[0];
            }
            else
                map = maps.FirstOrDefault(m => string.Equals(m.To, toGear.Trim(), StringComparison.OrdinalIgnoreCase))
                      ?? throw new ToolError($"{old.DLL.Name} has no mapping to {toGear}. Its successors: {string.Join(", ", maps.Select(m => m.To))}.");
            var dll = Dll(core, map.To) ?? throw new ToolError($"{map.To} isn't installed (gear_list shows what is).");
            if (dll.IsMissing || dll.IsCrashed) throw new ToolError($"{map.To} is {(dll.IsMissing ? "missing" : "crashed")} and can't be added.");
            var manifest = SongModel.Manifest(dll) ?? throw new ToolError($"{map.To} has no manifest beside it, so its values can't be set in units.");
            var oldManifest = ManifestOf(core, old.DLL);
            var olds = new List<SuccessorParam>();
            for (int g = 1; g <= 2 && g < old.ParameterGroups.Count; g++)
                foreach (var p in old.ParameterGroups[g].Parameters)
                {
                    if (p.Type == ParameterType.Internal) continue;
                    var entry = oldManifest?.FindAt(g == 2, p.IndexInGroup, p.Name);
                    if (entry != null && (entry.Min != p.MinValue || entry.Max != p.MaxValue)) entry = null;   // a stale manifest: trust the machine
                    olds.Add(new SuccessorParam
                    {
                        Name = p.Name, Track = g == 2, Index = p.IndexInGroup, Min = p.MinValue, Max = p.MaxValue, Default = p.DefValue,
                        Mapping = entry?.Mapping, Entry = entry,
                    });
                }
            var converter = new SuccessorConverter(map, olds, SuccessorParam.FromManifest(manifest))
            {
                // Delay lengths in ticks or samples (U5, Neo Delay's pairs): the song's tempo and rate now.
                Timing = new SuccessorTiming(Math.Max(1, core.BPM), Math.Max(1, core.TPB), Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec)),
            };
            if (converter.Errors.Count > 0)
                throw new ToolError($"The {map.Label} mapping doesn't fit {old.Name}: {string.Join(" ", converter.Errors)}");
            var plan = new Plan
            {
                Old = old,
                To = dll,
                Map = map,
                Converter = converter,
                OldPitchOffset = map.Notes?.PitchOffset ?? (oldManifest?.Notes is { Pitched: true } n ? n.PitchOffset : 0),
            };
            plan.State = converter.ConvertState(sp => Current(old, sp, 0));
            // A drum machine tuned by its notes (FSM KickXP): the rules read the song's most common note.
            if (map.Notes is { RulesReadNote: true } && converter.OldColumn(map.Notes.Note) is { } noteColumn
                && converter.MostCommonNote(old.Patterns.Select(p => (IReadOnlyDictionary<(SuccessorParam, int), IReadOnlyList<ColumnEvent>>)Columns(plan, p)), out int distinct) is int note)
            {
                plan.State = converter.ConvertState(sp => sp == noteColumn ? note : Current(old, sp, 0));
                if (distinct > 1)
                    plan.State.Notes.Add($"{old.Name}'s notes tune it, and they are at {distinct} pitches: {map.To}'s settings start at the most common, {Pitch.Name(TrackNotes.FromBuzz(note))}, and each note moves them as it plays (automation in the converted patterns).");
            }
            return plan;
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MachineManifest> byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The gear's manifest: beside its DLL, or, for a machine whose DLL isn't here (a placeholder for a closed native off
        /// Windows, mac-engine.md M2), the draft or curated one Gear carries for it all the same (AppGear.targets, build.ps1).
        /// </summary>
        static MachineManifest ManifestOf(ReBuzzCore core, IMachineDLL dll)
        {
            var manifest = SongModel.Manifest(dll);
            if (manifest != null || string.IsNullOrEmpty(dll?.Name)) return manifest;
            return byName.GetOrAdd(dll.Name, name =>
            {
                foreach (var dir in core.MachineDLLs.Values.Select(d => d.Path).Where(p => !string.IsNullOrEmpty(p))
                             .Select(System.IO.Path.GetDirectoryName).Where(d => d != null).Distinct())
                {
                    try
                    {
                        var found = MachineManifest.Load(System.IO.Path.Combine(dir, name + MachineManifest.Suffix));
                        if (found != null) return found;
                    }
                    catch (Exception) { }
                }
                return null;
            });
        }

        static IParameter Parameter(IMachine m, SuccessorParam sp)
        {
            int g = sp.Track ? 2 : 1;
            if (g >= m.ParameterGroups.Count) return null;
            var ps = m.ParameterGroups[g].Parameters;
            return ps.FirstOrDefault(p => p.IndexInGroup == sp.Index && p.Name == sp.Name) ?? ps.FirstOrDefault(p => string.Equals(p.Name, sp.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A parameter's value now (a track's for track parameters), or its default where it holds none.</summary>
        static int Current(MachineCore m, SuccessorParam sp, int track)
        {
            var p = Parameter(m, sp);
            if (p == null) return sp.Default;
            int v;
            try { v = p.GetValue(sp.Track ? Math.Min(track, Math.Max(0, m.TrackCount - 1)) : 0); }
            catch (Exception) { return sp.Default; }
            return v == p.NoValue || v < p.MinValue || v > p.MaxValue ? p.DefValue : v;
        }

        // ------------------------------------------------------------------ the report

        static JsonObject Report(Plan plan)
        {
            var map = plan.Map;
            var o = new JsonObject
            {
                ["machine"] = plan.Old.Name,
                ["from"] = map.From,
                ["to"] = map.To,
                ["summary"] = map.Summary,
                ["mapping"] = new JsonArray(plan.State.Values.Select(v => (JsonNode)v.Json()).ToArray()),
                ["unmapped"] = new JsonArray(map.Unmapped.Select(u => (JsonNode)new JsonObject { ["param"] = u.Param, ["why"] = u.Why }).ToArray()),
                ["connections"] = new JsonObject
                {
                    ["input_gain_db"] = Math.Round(plan.State.InputGainDb, 2),
                    ["output_gain_db"] = Math.Round(plan.State.OutputGainDb, 2),
                },
                ["remarks"] = new JsonArray(map.Remarks.Select(r => (JsonNode)r).ToArray()),
                ["tolerance"] = new JsonObject
                {
                    ["loudness_db"] = map.Tolerance.LoudnessDb,
                    ["band_db"] = map.Tolerance.BandDb is double b ? b : null,
                    ["from_hz"] = map.Tolerance.FromHz,
                    ["to_hz"] = map.Tolerance.ToHz,
                    ["measured"] = map.Tolerance.Measured,
                },
            };
            if (map.Tolerance.WhyNot.Length > 0) o["tolerance"]!["why_not"] = map.Tolerance.WhyNot;
            if (plan.State.Bypass == true) o["bypass"] = $"{map.To} is bypassed, as {plan.Old.Name}'s Bypass parameter was on.";
            if (map.Notes != null)
                o["notes_moved"] = $"{map.Notes.Why} Pitch convention: {map.From} {(plan.OldPitchOffset == 0 ? "plays as written" : $"plays {plan.OldPitchOffset:+0;-0} semitones")}, {map.To} as its manifest says.";
            return o;
        }

        // ------------------------------------------------------------------ the tool and the menu

        /// <summary>
        /// Replaces <paramref name="old"/> with its successor, as one undo step when the caller groups it (the agent's
        /// Engine call, or the UI's action group). Engine thread. <paramref name="dispatcher"/> and <paramref name="progress"/>
        /// serve the renders.
        /// </summary>
        public static JsonObject Run(ReBuzzCore core, IUiDispatcher dispatcher, MachineCore old, string toGear, ReplaceOptions o,
            CancellationToken cancellation = default, Action<double, double, string> progress = null)
        {
            var plan = MakePlan(core, old, toGear);
            var result = Report(plan);
            var notes = new List<string>(plan.State.Notes);
            notes.AddRange(plan.State.Values.Where(v => v.Note != null).Select(v => v.Note));
            string finalName = FinalName(core, old, o);
            if (o.DryRun)
            {
                result["dry_run"] = true;
                result["would_be_named"] = finalName;
                result["patterns"] = new JsonArray(old.Patterns.Select(p => (JsonNode)PatternSummary(core, plan, p, null, notes)).ToArray());
                result["notes"] = Strings(notes);
                result["expected"] = Expected(plan.Map);
                return result;
            }
            if (finalName != old.Name && SongModel.Machines(core).Any(m => string.Equals(m.Name, finalName, StringComparison.OrdinalIgnoreCase)))
                throw new ToolError($"There's already a machine named \"{finalName}\". Choose another name.");

            var warnings = new List<string>();
            var (from, to, rangeNote) = Range(core, o);
            StereoAudio beforeSong = null, beforeMachine = null, beforeInput = null;
            // The old machine's inputs at their gains, so each side is read against what went in (an effect's source may play differently each render).
            var inputAmps = old.Inputs.Where(c => !((MachineCore)c.Source).Hidden).GroupBy(c => c.Source.Name).ToDictionary(g => g.Key, g => g.First().Amp);
            if (o.Compare)
            {
                var probe = old.Outputs.Any(c => !((MachineCore)c.Destination).Hidden) ? new[] { old } : Array.Empty<MachineCore>();
                var taps = old.Inputs.Where(c => !((MachineCore)c.Source).Hidden).Select(c => ("in:" + c.Source.Name, c, (double)c.Amp)).ToList();
                // A render plays the patterns, which move the machine's parameters: its state goes back as it was, so the
                // song an undo brings back is the one there was.
                var state = SaveState(old);
                var (master, probes) = RenderTools.Render(core, dispatcher, from, to, o.TailSeconds, null, null, probe, cancellation, progress, warnings, taps);
                RestoreState(old, state);
                beforeSong = master;
                beforeMachine = probes.FirstOrDefault(p => p.Machine == old.Name).Audio;
                beforeInput = Sum(probes.Where(p => p.Machine.StartsWith("in:", StringComparison.Ordinal)).Select(p => p.Audio));
            }

            var made = Apply(core, plan, o, finalName, result, notes);

            if (o.Compare)
            {
                var probe = made.Outputs.Any(c => !((MachineCore)c.Destination).Hidden) ? new[] { made } : Array.Empty<MachineCore>();
                // Its inputs at the old connections' gains: the mapping's input gain belongs to the machine (a mono native summed left and right).
                var taps = made.Inputs.Where(c => !((MachineCore)c.Source).Hidden && inputAmps.ContainsKey(c.Source.Name))
                    .Select(c => ("in:" + c.Source.Name, c, (double)inputAmps[c.Source.Name])).ToList();
                var (master, probes) = RenderTools.Render(core, dispatcher, from, to, o.TailSeconds, null, null, probe, cancellation, progress, warnings, taps);
                var afterMachine = probes.FirstOrDefault(p => p.Machine == made.Name).Audio;
                var afterInput = Sum(probes.Where(p => p.Machine.StartsWith("in:", StringComparison.Ordinal)).Select(p => p.Audio));
                var comparison = new JsonObject
                {
                    ["from_beats"] = SongModel.Beats(core, from),
                    ["to_beats"] = SongModel.Beats(core, to),
                };
                if (rangeNote != null) comparison["range_note"] = rangeNote;
                JsonObject machine = null;
                if (beforeMachine != null && afterMachine != null)
                    comparison["machine"] = machine = AudioReport.Tolerance(beforeMachine, afterMachine, plan.Map.Tolerance, $"{old.Name}'s output", beforeInput, afterInput);
                var song = AudioReport.Tolerance(beforeSong, master, plan.Map.Tolerance, "the song");
                comparison["song"] = song;
                var verdict = machine ?? song;
                comparison["within_tolerance"] = verdict["within"]?.DeepClone();
                var differences = new JsonArray();
                if (song["within"] != null)
                    foreach (var read in AudioReport.Compare(beforeSong, master, "before", "after")["reads"]!.AsArray()) differences.Add(read!.DeepClone());
                comparison["differences"] = differences;
                if (verdict["within"] is JsonValue w && !w.GetValue<bool>())
                    comparison["why_not"] = plan.Map.Tolerance.WhyNot.Length > 0 ? plan.Map.Tolerance.WhyNot
                        : $"The {plan.Map.Label} conversion measures outside its tolerance on this material: " + string.Join(" ", verdict["reads"]!.AsArray().Select(x => x!.GetValue<string>()));
                if (warnings.Count > 0) comparison["warnings"] = Strings(warnings);
                result["comparison"] = comparison;
            }
            result["notes"] = Strings(notes);
            result["replaced_by"] = SongModel.MachineJson(core, made);
            return result;
        }

        static List<(IParameter Parameter, int Track, int Value)> SaveState(MachineCore m)
        {
            var list = new List<(IParameter, int, int)>();
            for (int g = 1; g <= 2 && g < m.ParameterGroups.Count; g++)
                foreach (var p in m.ParameterGroups[g].Parameters)
                {
                    if (p.Type == ParameterType.Internal || (p.Flags & ParameterFlags.State) == 0) continue;
                    for (int t = 0; t < (g == 2 ? Math.Max(1, m.TrackCount) : 1); t++)
                    {
                        try { list.Add((p, t, p.GetValue(t))); } catch (Exception) { }
                    }
                }
            return list;
        }

        static void RestoreState(MachineCore m, List<(IParameter Parameter, int Track, int Value)> state)
        {
            foreach (var (p, t, v) in state)
            {
                try { if (p.GetValue(t) != v) p.SetValue(t | (1 << 16), v); } catch (Exception) { }
            }
        }

        /// <summary>The sum of several captures of one render (a machine's inputs), or null for none.</summary>
        static StereoAudio Sum(IEnumerable<StereoAudio> parts)
        {
            StereoAudio sum = null;
            foreach (var a in parts.Where(a => a != null))
            {
                if (sum == null) { sum = new StereoAudio { Left = (float[])a.Left.Clone(), Right = (float[])(a.Right ?? a.Left).Clone(), Rate = a.Rate }; continue; }
                int n = Math.Min(sum.Frames, a.Frames);
                for (int i = 0; i < n; i++)
                {
                    sum.Left[i] += a.Left[i];
                    sum.Right[i] += (a.Right ?? a.Left)[i];
                }
            }
            return sum;
        }

        /// <summary>
        /// The machine menu's "Replace with …" (the studio and ReBuzz.exe): the same as the tool, one undo step (a failure
        /// takes back what it did), the song's first 30 s compared; the result in a message (a dialog), and returned.
        /// UI thread (the engine's).
        /// </summary>
        public static string FromMenu(ReBuzzCore core, MachineCore machine, string gear)
        {
            var stack = core.SongCore.ActionStack;
            int steps = 0;
            void Count(IEnumerable<IAction> actions, bool done) { if (done) steps++; }
            stack.BeginActionGroup();
            stack.PostDoOrUndoGroup += Count;
            JsonObject r = null;
            string error = null;
            try
            {
                r = Run(core, core.dispatcher, machine, gear, new ReplaceOptions { MaxSeconds = 30 });
            }
            catch (Exception e) { error = e is ToolError ? e.Message : $"{e.GetType().Name}: {e.Message}"; }
            finally
            {
                stack.PostDoOrUndoGroup -= Count;
                stack.EndActionGroup();
            }
            if (error != null)
            {
                if (steps > 0) { try { stack.Undo(); } catch (Exception) { } }
                core.UserMessages?.Info($"{machine.Name} wasn't replaced: {error}", "Replace");
                return null;
            }
            core.SetModifiedFlag();
            string text = ReplaceTools.Summary(r);
            core.UserMessages?.Info(text, $"Replaced with {gear}");
            return text;
        }

        static JsonArray Strings(IEnumerable<string> list) => new(list.Distinct().Select(x => (JsonNode)x).ToArray());

        static JsonObject Expected(SuccessorMap map)
        {
            var t = map.Tolerance;
            var o = new JsonObject
            {
                ["loudness"] = $"within ±{t.LoudnessDb.ToString("0.#", C)} dB",
                ["bands"] = t.BandDb is double b ? $"each third octave from {t.FromHz.ToString("0", C)} Hz to {(t.ToHz / 1000).ToString("0.#", C)} kHz within ±{b.ToString("0.#", C)} dB, level-matched" : "not checked",
            };
            if (t.Measured.Length > 0) o["measured"] = t.Measured;
            if (t.WhyNot.Length > 0) o["why_not"] = t.WhyNot;
            return o;
        }

        static string FinalName(ReBuzzCore core, MachineCore old, ReplaceOptions o)
        {
            if (!string.IsNullOrWhiteSpace(o.Name)) return o.Name.Trim();
            return o.KeepOld ? Free(core, old.Name + " (new)") : old.Name;
        }

        static string Free(ReBuzzCore core, string wanted)
        {
            bool Taken(string n) => SongModel.Machines(core).Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase));
            if (!Taken(wanted)) return wanted;
            for (int i = 2; ; i++)
                if (!Taken($"{wanted} {i}")) return $"{wanted} {i}";
        }

        static (int From, int To, string Note) Range(ReBuzzCore core, ReplaceOptions o)
        {
            var song = core.SongCore;
            double ticksPerSecond = core.BPM * (double)Math.Max(1, core.TPB) / 60.0;
            int max = Math.Max(1, (int)Math.Round(o.MaxSeconds * ticksPerSecond));
            int from = o.From ?? 0;
            int to = o.To ?? song.SongEnd;
            string note = null;
            if (to <= from)
            {
                to = from + Math.Min(max, Math.Max(1, core.TPB) * 16);
                note = "The song has no length here: compared 16 beats.";
            }
            if (o.To == null && to - from > max)
            {
                to = from + max;
                note = $"Compared the first {o.MaxSeconds.ToString("0", C)} s from beat {SongModel.Beats(core, from).ToString("0.##", C)}; give from and to for another part.";
            }
            return (from, to, note);
        }

        // ------------------------------------------------------------------ the change

        static MachineCore Find(ReBuzzCore core, string name) => core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);

        static MachineCore Apply(ReBuzzCore core, Plan plan, ReplaceOptions o, string finalName, JsonObject result, List<string> notes)
        {
            var song = core.SongCore;
            var old = plan.Old;
            string oldName = old.Name;
            bool sameName = finalName == oldName;
            string tempName = sameName ? Free(core, $"{oldName} ({plan.To.Name})") : finalName;
            float x = old.Position.Item1, y = old.Position.Item2 + (o.KeepOld ? 0.12f : 0);

            // The successor.
            var before = SongModel.Machines(core).ToHashSet();
            song.CreateMachine(plan.To.Name, null, tempName, null, null, null, -1, x, y);
            var made = SongModel.Machines(core).FirstOrDefault(m => !before.Contains(m)) ?? throw new ToolError($"{Product.Name} didn't create {plan.To.Name}.");

            // Its settings.
            foreach (var (sp, raw) in plan.State.Final)
            {
                var p = Parameter(made, sp);
                if (p == null) continue;
                if (sp.Track)
                    for (int t = 0; t < Math.Max(1, made.TrackCount); t++) AgentActions.SetParameter(core, made, p, t, raw);
                else
                    AgentActions.SetParameter(core, made, p, 0, raw);
            }

            // Its connections: the old ones', with the mapping's gains.
            int Scaled(int amp, double db, string what)
            {
                double v = amp * Math.Pow(10, db / 20);
                if (v > Levels.MaxAmp + 0.5)
                    notes.Add($"{what}: {Levels.Text(Levels.AmpToDb(amp) + db)} is more than a connection takes (+12 dB); set to +12 dB.");
                return Math.Clamp((int)Math.Round(v), 0, Levels.MaxAmp);
            }
            int ins = 0, outs = 0;
            foreach (var c in old.Inputs.Where(c => !((MachineCore)c.Source).Hidden).ToList())
            {
                var src = (MachineCore)c.Source;
                if (!song.CanConnectMachines(src, made)) { notes.Add($"{src.Name} can't connect to {made.Name}."); continue; }
                int channel = c.DestinationChannel < Math.Max(1, made.InputChannelCount) ? c.DestinationChannel : 0;
                if (channel != c.DestinationChannel) notes.Add($"{src.Name} fed {oldName}'s input {c.DestinationChannel + 1}; {plan.To.Name} has fewer, so it feeds its first.");
                song.ConnectMachines(src, made, new[] { c.SourceChannel }, new[] { channel }, Scaled(c.Amp, plan.State.InputGainDb, $"{src.Name} → {finalName}"), c.HasPan ? c.Pan : Levels.PanCenter);
                ins++;
            }
            foreach (var c in old.Outputs.Where(c => !((MachineCore)c.Destination).Hidden).ToList())
            {
                var dst = (MachineCore)c.Destination;
                if (!song.CanConnectMachines(made, dst)) { notes.Add($"{made.Name} can't connect to {dst.Name}."); continue; }
                song.ConnectMachines(made, dst, new[] { 0 }, new[] { c.DestinationChannel }, Scaled(c.Amp, plan.State.OutputGainDb, $"{finalName} → {dst.Name}"), c.Pan);
                outs++;
            }

            // Mute, bypass.
            bool effect = SongModel.Kind(made) == "effect";
            if (effect && (old.IsBypassed || plan.State.Bypass == true)) AgentActions.SetFlag(core, made, AgentActions.Flag.Bypass, true);
            if (old.IsMuted && !old.IsDiverged) AgentActions.SetFlag(core, made, AgentActions.Flag.Mute, true);

            // Its patterns.
            var patterns = new JsonArray();
            foreach (var p in old.Patterns.ToList())
            {
                var existing = made.Patterns.FirstOrDefault(x => x.Name == p.Name);
                if (existing == null) AgentActions.Do(core, new CreatePatternAction(made, p.Name, p.Length));
                var np = made.Patterns.FirstOrDefault(x => x.Name == p.Name);
                if (np == null) { notes.Add($"Pattern {p.Name} couldn't be made on {made.Name}."); continue; }
                patterns.Add(PatternSummary(core, plan, p, np, notes));
            }

            // The arrangement: each old sequence gets one of the successor's beside it, with the same events.
            var sequences = song.Sequences.Where(s => s.Machine == old).ToList();
            int inserted = 0;
            foreach (var s in sequences)
            {
                int at = song.Sequences.IndexOf(s);
                var events = s.Events.Select(e => (e.Key, e.Value.Type, Pattern: e.Value.Pattern?.Name, e.Value.Span, e.Value.Offset, e.Value.End, e.Value.Gain, e.Value.Clip)).ToList();
                InsertSequence(core, tempName, at, events, s.IsDisabled);
                inserted++;
            }
            // The Session View's tracks and clip settings follow (it knows machines by name).
            if (core.SessionLayout?.Document != null)
            {
                SessionLayout.Adoption adoption = null;
                AgentActions.Do(core, new DelegateAction(() => adoption = core.SessionLayout?.Adopt(oldName, tempName), () => core.SessionLayout?.Unadopt(adoption)));
            }

            // The old machine goes (or stays, muted), and the successor takes its name.
            if (o.KeepOld)
            {
                if (!old.IsMuted) AgentActions.SetFlag(core, old, AgentActions.Flag.Mute, true);
            }
            else
                song.DeleteMachines(new IMachine[] { old });
            if (made.Name != finalName) song.RenameMachineUndoable(made, finalName);

            result["old"] = o.KeepOld ? $"kept, muted: {oldName}" : "removed";
            result["patterns"] = patterns;
            result["sequences"] = inserted;
            result["connections"]!["inputs"] = ins;
            result["connections"]!["outputs"] = outs;
            return made;
        }

        /// <summary>One pattern: what moves (a dry run: <paramref name="target"/> null) or what moved.</summary>
        static JsonObject PatternSummary(ReBuzzCore core, Plan plan, IPattern p, IPattern target, List<string> notes)
        {
            var columns = Columns(plan, p);
            var conv = plan.Converter;
            var converted = conv.ConvertEvents(plan.State, (sp, track) => Current(plan.Old, sp, track), columns, notes);
            double secondsPerTick = 60.0 / (Math.Max(1, core.BPM) * (double)Math.Max(1, core.TPB));
            var glides = new List<(int Time, int Raw)>();
            var noteList = conv.ConvertNotes(columns, p.Length * NoteList.TimeBase, -plan.OldPitchOffset, secondsPerTick, (sp, track) => Current(plan.Old, sp, track), glides, notes);
            // MIDI notes (the engine's MIDI column, the pattern editor's note lanes) move as they are, in pitch as heard.
            var midi = MidiNotes(core, plan.Old, p).Select(n => n with { Pitch = n.Pitch - plan.OldPitchOffset }).Where(n => n.Pitch >= 0 && n.Pitch <= 127).ToList();
            noteList.AddRange(midi);
            noteList.Sort(NoteList.Order);
            if (glides.Count > 0 && conv.New(plan.Map.Notes?.Glide) is { } glide)
            {
                var list = converted.TryGetValue((glide, 0), out var l) ? l : converted[(glide, 0)] = new List<ColumnEvent>();
                foreach (var (time, raw) in glides) if (!list.Any(e => e.Time == time)) list.Add(new ColumnEvent(time, raw));
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            if (noteList.Count > 0 && plan.Map.Notes == null && midi.Count == 0)
                notes.Add($"Pattern {p.Name}'s notes have nowhere to go: the {plan.Map.Label} mapping moves no notes.");
            var o = new JsonObject
            {
                ["pattern"] = p.Name,
                ["length_beats"] = SongModel.Beats(core, p.Length),
                ["columns"] = converted.Count,
                ["events"] = converted.Values.Sum(v => v.Count),
                ["notes"] = noteList.Count,
            };
            if (target == null || (converted.Count == 0 && noteList.Count == 0)) return o;

            var made = (MachineCore)target.Machine;
            foreach (var ((sp, track), events) in converted)
            {
                var np = Parameter(made, sp);
                if (np == null || events.Count == 0) continue;
                var key = new ColumnKey(sp.Track ? 2 : 1, np.IndexInGroup, sp.Track ? track : 0);
                AgentActions.ReplaceColumnEvents(core, target, key, Array.Empty<PatternEvent>(), events.Select(e => new PatternEvent(e.Time, e.Raw, e.Duration)).ToList());
            }
            if (noteList.Count > 0)
            {
                var problems = new List<string>();
                try { MachineNotes.Replace(core, target, MachineNotes.For(made), Array.Empty<NoteEvent>(), noteList, problems); }
                catch (ToolError e) { notes.Add($"Pattern {p.Name}'s notes: {e.Message}"); }
                notes.AddRange(problems);
            }
            return o;
        }

        /// <summary>
        /// The old pattern's parameter columns of its own machine: the engine-played ones (automation, track notes) and
        /// the pattern editor's tracker columns (the k-th column of a track parameter is track k), by parameter and track.
        /// </summary>
        static Dictionary<(SuccessorParam Param, int Track), IReadOnlyList<ColumnEvent>> Columns(Plan plan, IPattern p)
        {
            var conv = plan.Converter;
            var lists = new Dictionary<(SuccessorParam, int), List<ColumnEvent>>();
            SuccessorParam Of(IParameter param) =>
                conv.OldParams.FirstOrDefault(sp => sp.Track == (param.Group.Type == ParameterGroupType.Track) && sp.Index == param.IndexInGroup && sp.Name == param.Name);
            void Add(IParameter param, int track, IEnumerable<PatternEvent> events)
            {
                if (param?.Group == null || !ReferenceEquals(param.Group.Machine, plan.Old) || param.IndexInGroup < 0) return;
                var sp = Of(param);
                if (sp == null) return;
                var key = (sp, sp.Track ? track : 0);
                var list = lists.TryGetValue(key, out var l) ? l : lists[key] = new List<ColumnEvent>();
                foreach (var e in events)
                    if (!list.Any(x => x.Time == e.Time)) list.Add(new ColumnEvent(e.Time, e.Value, Math.Max(0, e.Duration)));
            }
            foreach (var c in p.Columns.ToList())
                if (c.Type == PatternColumnType.Parameter && c.Parameter != null)
                    Add(c.Parameter, c.Track, c.GetEvents(int.MinValue, int.MaxValue));
            List<IPatternEditorColumn> editor;
            try { editor = p.PatternEditorMachineEvents?.ToList() ?? new List<IPatternEditorColumn>(); }
            catch (Exception) { editor = new List<IPatternEditorColumn>(); }
            var seen = new Dictionary<IParameter, int>();
            foreach (var c in editor)
            {
                var param = c?.Parameter;
                if (param == null) continue;
                int k = seen.TryGetValue(param, out int n) ? n : 0;
                seen[param] = k + 1;
                Add(param, param.Group?.Type == ParameterGroupType.Track ? k : 0, c.Events ?? Enumerable.Empty<PatternEvent>());
            }
            return lists.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ColumnEvent>)kv.Value.OrderBy(e => e.Time).ToList());
        }

        /// <summary>The pattern's MIDI notes as stored: the engine's MIDI column and the pattern editor's note lanes.</summary>
        static List<NoteEvent> MidiNotes(ReBuzzCore core, MachineCore old, IPattern p)
        {
            var list = SongModel.MidiColumnNotes(p);
            if (p is PatternCore pc)
            {
                int[] triples = null;
                try { triples = core.MachineManager.ExportClipNotes(old, pc); }
                catch (Exception) { }
                if (triples != null)
                    for (int i = 0; i + 2 < triples.Length; i += 3)
                        if (NoteEvent.TryFrom(triples[i], triples[i + 1], triples[i + 2], out var n)) list.Add(n);
            }
            return list;
        }

        /// <summary>A sequence for <paramref name="machine"/> at <paramref name="index"/>, with these events (patterns by name), as an undo step.</summary>
        static void InsertSequence(ReBuzzCore core, string machine, int index,
            List<(int Time, SequenceEventType Type, string Pattern, int Span, int Offset, int End, double Gain, int Clip)> events, bool disabled)
        {
            ISequence made = null;
            void Do()
            {
                var m = Find(core, machine);
                if (m == null) return;
                var song = core.SongCore;
                int at = Math.Clamp(index, 0, song.Sequences.Count);
                song.AddSequence(m, at);
                var seq = song.Sequences[at];
                foreach (var e in events)
                {
                    var pattern = e.Pattern == null ? null : m.Patterns.FirstOrDefault(x => x.Name == e.Pattern);
                    if (e.Pattern != null && pattern == null) continue;
                    seq.SetEvent(e.Time, new SequenceEvent(e.Type, pattern, e.Span) { Offset = e.Offset, End = e.End, Gain = e.Gain, Clip = e.Clip });
                }
                if (disabled) seq.IsDisabled = true;
                // A redo makes it again: the Session View gives it back the track it had (0.6.2).
                if (made != null) Restorations.Report(made, seq);
                made = seq;
            }
            void Undo()
            {
                var song = core.SongCore;
                var seq = made != null && song.Sequences.Contains(made) ? made : null;
                if (seq != null) song.RemoveSequence(seq);
            }
            AgentActions.Do(core, new DelegateAction(Do, Undo));
        }
    }
}
