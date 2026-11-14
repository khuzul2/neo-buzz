// Evaluation (docs/specs/agent-knowledge.md §3, K5): the knowledge tasks. One task per guide and per recipe
// the agent's guides hold, plus questions a person asks ("which machine for X", "set Y to Z"). Each is a
// short brief, a setup (a song to start from), a scripted reference solution (the tool calls a competent
// agent makes after reading the guide: the recipes' own steps are replayed from the guide text), and a
// deterministic check of the song it saves: machines, parameter values read back in real units, notes,
// the arrangement, the session grid, the song's notes, a render that is not silent. No judgement by a
// model anywhere. The briefs say what the person wants, never which guide to read: finding the guide
// (the `guide` tool, rebuzz://catalog, machine_find) is part of the task.
//
// A task here has four to six criteria and the starting song fails at least half of them, so the
// reference solution passing and the starting song not passing keep proving the scorer end to end,
// like the older tasks.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReBuzz.Agent.Guides;

namespace ReBuzz.AgentEval
{
    /// <summary>Collects a task's criteria while it checks the song.</summary>
    internal sealed class Ck
    {
        public readonly List<Criterion> List = new();
        public void Add(string name, bool ok, string detail) => List.Add(new Criterion(name, ok ? 1 : 0, detail));
        public void Add(string name, double score, string detail) => List.Add(new Criterion(name, score, detail));
    }

    internal sealed class KnowledgeTask : EvalTask
    {
        readonly string name, title, kind, covers;
        readonly string goal;
        readonly Action<Studio, TaskContext> setup, solve;
        readonly Action<Studio, TaskContext, Ck> check;

        /// <param name="kind">guide, recipe or ask.</param>
        /// <param name="covers">The guide topic or recipe id it exercises ("" for a question).</param>
        public KnowledgeTask(string kind, string name, string title, string covers, string goal,
            Action<Studio, TaskContext> setup, Action<Studio, TaskContext> solve, Action<Studio, TaskContext, Ck> check)
        {
            this.kind = kind;
            this.name = name;
            this.title = title;
            this.covers = covers;
            this.goal = goal;
            this.setup = setup;
            this.solve = solve;
            this.check = check;
        }

        public override string Name => name;
        public override string Title => title;
        public string Kind => kind;
        public string Covers => covers;

        public override string Brief(TaskContext ctx) =>
            goal.Replace("{dir}", ctx.Folder.Replace("\\", "/")) +
            $" When you are done, save the song with song_save(path=\"{ctx.ResultSong.Replace("\\", "/")}\", overwrite=true).";

        public override void Setup(Studio s, TaskContext ctx) => setup?.Invoke(s, ctx);
        public override void Solve(Studio s, TaskContext ctx) => solve(s, ctx);

        public override Facts Measure(Studio s, TaskContext ctx)
        {
            var ck = new Ck();
            try { check(s, ctx, ck); }
            catch (Exception e) { ck.Add("check_error", false, $"{e.GetType().Name}: {e.Message}"); }
            var f = new Facts();
            f.Text("criteria", string.Join(",", ck.List.Select(c => c.Name)));
            foreach (var c in ck.List)
            {
                f["c." + c.Name] = c.Score;
                f.Text("d." + c.Name, c.Detail);
            }
            return f;
        }

        public override List<Criterion> Score(Facts start, Facts result, TaskContext ctx) =>
            (result.Text("criteria") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => new Criterion(n, result["c." + n], result.Text("d." + n))).ToList();
    }

    /// <summary>What the knowledge tasks share: reading the song, and the recipes replayed from the guide.</summary>
    internal static class K
    {
        // ---- reading the song ------------------------------------------------------------------------

        /// <summary>A parameter as param_get reads it (value, number, unit), or null when the machine or parameter is missing.</summary>
        public static JsonObject Param(Studio s, string machine, string param)
        {
            if (machine == null) return null;
            var c = s.C.Call("param_get", new JsonObject { ["machine"] = machine, ["params"] = J.A(param) });
            return c.IsError ? null : J.Objects(c.Data["values"]).FirstOrDefault();
        }

        public static string Text(JsonObject p) => p == null ? null : J.Str(p["value"]);
        public static double Num(JsonObject p) => p == null ? double.NaN : J.Num(p["number"]);

        /// <summary>The first machine of that gear (the name the agent gave it may differ from the brief's).</summary>
        public static Mach Gear(Graph g, string gear, string preferred = null) =>
            (preferred != null ? g.Machines.FirstOrDefault(m => m.Gear == gear && string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase)) : null)
            ?? g.Machines.FirstOrDefault(m => m.Gear == gear);

        /// <summary>A parameter check: <paramref name="want"/> within the relative tolerance (or the absolute one for values near 0).</summary>
        public static void Value(Ck ck, string criterion, Studio s, string machine, string param, double want, double rel = 0.03, double abs = 0.0, string unit = "")
        {
            var p = Param(s, machine, param);
            double got = Num(p);
            bool ok = !double.IsNaN(got) && Math.Abs(got - want) <= Math.Max(Math.Abs(want) * rel, abs);
            ck.Add(criterion, ok, p == null ? $"{machine ?? "no machine"}: {param} not readable" : $"{param} reads {Text(p)} ({J.F(got, 4)} {J.Str(p["unit"])}), wanted {want}{unit}");
        }

        public static void Choice(Ck ck, string criterion, Studio s, string machine, string param, string want)
        {
            var p = Param(s, machine, param);
            ck.Add(criterion, p != null && string.Equals(Text(p), want, StringComparison.OrdinalIgnoreCase),
                p == null ? $"{machine ?? "no machine"}: {param} not readable" : $"{param} reads {Text(p)}, wanted {want}");
        }

        public static string SongNotes(Studio s)
        {
            var c = s.C.Call("song_notes_read");
            return c.IsError ? "" : J.Str(c.Data["notes"]) ?? "";
        }

        public static double Tempo(Studio s) => J.Num(s.Status()["tempo_bpm"]);

        /// <summary>The notes a machine plays in the arrangement, as (pitch, start) pairs.</summary>
        public static List<(int Pitch, double Start)> Placed(Studio s, string machine) =>
            machine == null ? new() : s.PlacedNotes(machine, s.C.Ok("arrangement_get"), s.Notes())
                .Select(n => ((int)J.Num(n["pitch"]), Math.Round(J.Num(n["start"]), 3))).OrderBy(n => n.Item2).ThenBy(n => n.Item1).ToList();

        public static int Pitch(string name) => ReBuzz.Agent.Model.Pitch.Parse(name);

        /// <summary>The whole song's render is not silent; its true peak is returned (NaN when silent).</summary>
        public static (bool Audible, double TruePeak) Audible(Studio s, double toBeats = 0)
        {
            var args = new JsonObject { ["tail_seconds"] = 0.5, ["name"] = "eval" };
            if (toBeats > 0) { args["from"] = 0; args["to"] = toBeats; }
            var mix = s.Render(args);
            bool audible = !Meters.Silent(mix.Audio);
            double tp = J.Num(mix.Data?["measure"]?["peaks"]?["true_peak_dbtp"]);
            return (audible, audible ? tp : double.NaN);
        }

        public static void Sounds(Ck ck, Studio s, double maxTruePeak = 0, double toBeats = 0)
        {
            var (audible, tp) = Audible(s, toBeats);
            ck.Add("sounds", audible && tp <= maxTruePeak, audible ? $"true peak {J.F(tp, 1)} dBTP (limit {maxTruePeak})" : "the render is silent");
        }

        /// <summary>The share of the explanation's longer words (five letters and more) found in the text.</summary>
        public static double WordOverlap(string explanation, string text)
        {
            var words = Regex.Matches((explanation ?? "").ToLowerInvariant(), @"[a-z]{5,}").Select(m => m.Value).Distinct().ToList();
            if (words.Count == 0) return 0;
            string t = (text ?? "").ToLowerInvariant();
            return words.Count(w => t.Contains(w)) / (double)words.Count;
        }

        // ---- the recipes, replayed from the guide ----------------------------------------------------------------

        sealed record Recipe(string Id, string Needs, List<(string Tool, JsonObject Args)> Calls);

        public static List<string> RecipeIds() => Recipes().Select(r => r.Id).ToList();

        static List<Recipe> Recipes()
        {
            string guide = GuideLibrary.Get("recipes") ?? throw new InvalidOperationException("the recipes guide is not embedded");
            guide = guide.Replace("\r\n", "\n");
            var list = new List<Recipe>();
            foreach (Match m in Regex.Matches(guide, @"^### (\S+)\n(.*?)(?=^### |^## )", RegexOptions.Multiline | RegexOptions.Singleline))
            {
                string body = m.Groups[2].Value;
                var calls = new List<(string, JsonObject)>();
                foreach (Match step in Regex.Matches(body, @"^\d+\. `([a-z_]+)\((.*)\)`$", RegexOptions.Multiline))
                    calls.Add((step.Groups[1].Value, ParseArgs(step.Groups[2].Value)));
                list.Add(new Recipe(m.Groups[1].Value, Regex.Match(body, @"Needs: ([a-z-]+)\.").Groups[1].Value, calls));
            }
            return list;
        }

        /// <summary>The arguments of a call as the guides write them: name=value, name=value, each value JSON.</summary>
        static JsonObject ParseArgs(string text)
        {
            var args = new JsonObject();
            var pieces = new List<string>();
            int depth = 0;
            bool quoted = false;
            var piece = new StringBuilder();
            foreach (char c in text)
            {
                if (c == '"') quoted = !quoted;
                if (!quoted)
                {
                    if (c is '[' or '{') depth++;
                    else if (c is ']' or '}') depth--;
                    else if (c == ',' && depth == 0) { pieces.Add(piece.ToString()); piece.Clear(); continue; }
                }
                piece.Append(c);
            }
            if (piece.ToString().Trim().Length > 0) pieces.Add(piece.ToString());
            foreach (var p in pieces)
            {
                var m = Regex.Match(p, @"^\s*([a-z_][a-z0-9_]*)\s*=\s*(.+?)\s*$", RegexOptions.Singleline);
                if (!m.Success) throw new InvalidOperationException($"a recipe argument written name=value: {p}");
                args[m.Groups[1].Value] = JsonNode.Parse(m.Groups[2].Value);
            }
            return args;
        }

        /// <summary>Runs a recipe's steps (the one it needs first, unless <paramref name="withNeeds"/> is false); <paramref name="skipLast"/> leaves off its last steps.</summary>
        public static void Replay(Studio s, string id, int skipLast = 0, bool withNeeds = true)
        {
            var all = Recipes();
            var recipe = all.FirstOrDefault(r => r.Id == id) ?? throw new InvalidOperationException($"no recipe {id} in the guide");
            if (withNeeds && recipe.Needs.Length > 0) Replay(s, recipe.Needs);
            var renders = new List<string>();
            foreach (var (tool, written) in recipe.Calls.Take(recipe.Calls.Count - skipLast))
            {
                var args = (JsonObject)JsonNode.Parse(written.ToJsonString())!;
                if (args["source"] is JsonValue sv && sv.TryGetValue(out string src) && Regex.Match(src, @"^r(\d+)$") is { Success: true } rm)
                    args["source"] = renders[int.Parse(rm.Groups[1].Value) - 1];
                var data = s.C.Ok(tool, args);
                if (tool == "render") renders.Add(J.Str(data["id"]));
            }
        }
    }
}
