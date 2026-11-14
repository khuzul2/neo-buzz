// Fork (docs/specs/agent-knowledge.md §9, K4): the recipes guide (ReBuzz.Agent/Guides/recipes.md) runs. Each recipe's step list is
// read from the guide itself, every call is made on the studio's agent server exactly as the guide writes it (headless, the real
// engine and the real Neo machines), and the recipe's result is checked. A recipe that "Needs" another runs that one first.
// The portable tests (ReBuzz.AgentTests/GuideTests) check that every tool, argument and parameter the guide names exists.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using ReBuzz.Agent.Guides;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class RecipeTests
    {
        static StudioHost Host => Studio.Host;

        sealed record Step(string Tool, JsonObject Args, bool Error, string Text, JsonObject Data);

        // ---- the guide ------------------------------------------------------------------------------

        sealed record Recipe(string Id, string Needs, List<(string Tool, JsonObject Args)> Calls);

        static List<Recipe> Recipes()
        {
            string guide = GuideLibrary.Get("recipes");
            Assert.That(guide, Is.Not.Null, "the recipes guide is embedded");
            guide = guide.Replace("\r\n", "\n");   // a Windows checkout may have CRLF
            var list = new List<Recipe>();
            foreach (Match m in Regex.Matches(guide, @"^### (\S+)\n(.*?)(?=^### |^## )", RegexOptions.Multiline | RegexOptions.Singleline))
            {
                string body = m.Groups[2].Value;
                string needs = Regex.Match(body, @"Needs: ([a-z-]+)\.").Groups[1].Value;
                var calls = new List<(string, JsonObject)>();
                foreach (Match step in Regex.Matches(body, @"^\d+\. `([a-z_]+)\((.*)\)`$", RegexOptions.Multiline))
                    calls.Add((step.Groups[1].Value, ParseArgs(step.Groups[2].Value)));
                list.Add(new Recipe(m.Groups[1].Value, needs, calls));
            }
            return list;
        }

        /// <summary>The arguments of a call as the guides write them: <c>name=value, name=value</c>, each value JSON.</summary>
        internal static JsonObject ParseArgs(string text)
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
                Assert.That(m.Success, Is.True, $"an argument written name=value: {p}");
                args[m.Groups[1].Value] = JsonNode.Parse(m.Groups[2].Value);
            }
            return args;
        }

        // ---- the server ------------------------------------------------------------------------------

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static MainWindow NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle(window);
            return window;
        }

        /// <summary>Runs a recipe (after the one it needs) and returns its steps; every step must succeed.</summary>
        static List<Step> Run(string id, List<Recipe> all, List<string> renders = null)
        {
            var recipe = all.Single(r => r.Id == id);
            var steps = new List<Step>();
            renders ??= new List<string>();
            if (recipe.Needs.Length > 0) Run(recipe.Needs, all, new List<string>());
            Assert.That(recipe.Calls.Count, Is.GreaterThanOrEqualTo(3), id);
            var own = new List<string>();
            foreach (var (tool, written) in recipe.Calls)
            {
                // "r1" in the guide is the id the recipe's first render returned.
                var args = (JsonObject)JsonNode.Parse(written.ToJsonString())!;
                if (args["source"] is JsonValue s && s.TryGetValue(out string src) && Regex.Match(src, @"^r(\d+)$") is { Success: true } rm)
                    args["source"] = own[int.Parse(rm.Groups[1].Value) - 1];
                var (error, text, data) = Try(tool, args);
                Assert.That(error, Is.False, $"{id}: {tool}({written.ToJsonString()}) failed: {text}");
                TestContext.Out.WriteLine($"{id}: {tool}: {text}");
                if (tool == "render") own.Add(data["id"]!.GetValue<string>());
                steps.Add(new Step(tool, args, error, text, data));
            }
            return steps;
        }

        static void Check(string id, Action<List<Step>> check)
        {
            var window = NewSong();
            using var restore = Studio.AfterTest(() =>
            {
                try { Try("session_launch", new JsonObject { ["stop"] = true, ["immediate"] = true }); } catch { }
                Dialogs.Responder = null;
                window.Close();
            });
            var steps = Run(id, Recipes());
            check(steps);
        }

        static Step Of(List<Step> steps, string tool, int nth = 0) => steps.Where(s => s.Tool == tool).ElementAt(nth);

        static void NotSilent(Step render) =>
            Assert.That(render.Data["measure"]!["silent"]!.GetValue<bool>(), Is.False, "the render is not silent: " + render.Text);

        // ---- the recipes -----------------------------------------------------------------------------

        [AvaloniaTest]
        public void EveryRecipeInTheGuideHasATestHere()
        {
            var ids = Recipes().Select(r => r.Id).ToList();
            Assert.That(ids, Is.EquivalentTo(new[]
            {
                "hear-a-bass", "movement-from-a-modulator", "safe-experiment", "arrange-a-section", "two-scenes",
                "controller-for-the-cutoff", "explain-a-control", "engine-swap", "replace-a-classic", "check-the-load",
            }));
            foreach (var r in Recipes())
                if (r.Needs.Length > 0) Assert.That(ids, Does.Contain(r.Needs), r.Id);
        }

        [AvaloniaTest]
        public void HearABass() => Check("hear-a-bass", steps =>
        {
            NotSilent(Of(steps, "render"));
            var peaks = Of(steps, "analyze").Data;
            Assert.That(peaks.ToJsonString(), Does.Contain("true_peak"), "the analysis measured peaks");
            Assert.That(Of(steps, "analyze").Text, Does.Not.Contain("silent"));
            Assert.That(Of(steps, "arrangement_place").Data["placed_at"]!.AsArray().Single()!.GetValue<double>(), Is.EqualTo(0));
        });

        [AvaloniaTest]
        public void MovementFromAModulator() => Check("movement-from-a-modulator", steps =>
        {
            var list = Of(steps, "modulation_list").Text;
            Assert.That(list, Does.Contain("F1 cutoff").And.Contain("Wobble"));
            NotSilent(Of(steps, "render"));
            var after = Try("modulation_list", new JsonObject { ["machine"] = "Bass" });
            Assert.That(after.Text, Does.Not.Contain("F1 cutoff"), "removed: " + after.Text);
        });

        [AvaloniaTest]
        public void SafeExperiment() => Check("safe-experiment", steps =>
        {
            Assert.That(Of(steps, "snapshot_diff").Data.ToJsonString(), Does.Contain("F1 cutoff"));
            var read = Of(steps, "param_get").Data["values"]![0]!;
            Assert.That(read["number"]!.GetValue<double>(), Is.LessThan(1000), "the restore put the cutoff back from 3 kHz: " + Of(steps, "param_get").Text);
        });

        [AvaloniaTest]
        public void ArrangeASection() => Check("arrange-a-section", steps =>
        {
            var markers = Of(steps, "arrangement_markers").Data.ToJsonString();
            Assert.That(markers, Does.Contain("Intro").And.Contain("Main"));
            var get = Of(steps, "arrangement_get").Data.ToJsonString();
            Assert.That(Regex.Matches(get, "\"pattern\":\"Line\"").Count, Is.GreaterThanOrEqualTo(3), get);
            NotSilent(Of(steps, "render"));
        });

        [AvaloniaTest]
        public void TwoScenes() => Check("two-scenes", steps =>
        {
            var grid = Of(steps, "session_get").Data;
            var tracks = grid["tracks"]!.AsArray();
            Assert.That(tracks.Single(t => t!["machine"]!.GetValue<string>() == "Bass")!["slots"]![0]!.GetValue<string>(), Is.EqualTo("Line"));
            Assert.That(tracks.Single(t => t!["machine"]!.GetValue<string>() == "Drums")!["slots"]![1]!.GetValue<string>(), Is.EqualTo("Beat"));
            Assert.That(grid["scenes"]!.AsArray().Select(s => s!["name"]?.GetValue<string>()).Where(n => n != null), Is.EquivalentTo(new[] { "Intro", "Groove" }));
            Assert.That(Of(steps, "session_launch", 0).Text, Is.Not.Empty);
        });

        [AvaloniaTest]
        public void ControllerForTheCutoff() => Check("controller-for-the-cutoff", steps =>
        {
            Assert.That(Of(steps, "midi_bindings_list", 0).Data.ToJsonString(), Does.Contain("F1 cutoff").And.Contain("74"));
            Assert.That(Of(steps, "midi_bindings_list", 1).Data.ToJsonString(), Does.Not.Contain("F1 cutoff"));
        });

        [AvaloniaTest]
        public void ExplainAControl() => Check("explain-a-control", steps =>
        {
            var row = Of(steps, "machine_describe").Data.ToJsonString();
            Assert.That(row, Does.Contain("\"how\"").And.Contain("\"sound\"").And.Contain("fm_index"));
            Assert.That(Of(steps, "param_get").Data["values"]![0]!["number"]!.GetValue<double>(), Is.EqualTo(60).Within(0.5));
        });

        [AvaloniaTest]
        public void EngineSwap() => Check("engine-swap", steps =>
        {
            NotSilent(Of(steps, "render"));
            Assert.That(Of(steps, "analyze").Text, Does.Contain("very dynamic"));
            Assert.That(Of(steps, "param_get").Text, Does.Contain("Modal").And.Contain("Bar"));
        });

        [AvaloniaTest]
        public void ReplaceAClassic() => Check("replace-a-classic", steps =>
        {
            Assert.That(Of(steps, "machine_replace", 0).Data["to"]!.GetValue<string>(), Is.EqualTo("Neo Verb"));
            Assert.That(Of(steps, "machine_replace", 1).Data["comparison"], Is.Not.Null, "the renders were compared: " + Of(steps, "machine_replace", 1).Text);
            Assert.That(Of(steps, "machine_list", 0).Data.ToJsonString(), Does.Contain("Neo Verb"));
            Assert.That(Of(steps, "machine_list", 1).Data.ToJsonString(), Does.Contain("Pedal Plate"));
        });

        [AvaloniaTest]
        public void CheckTheLoad() => Check("check-the-load", steps =>
        {
            var cpu = Of(steps, "render").Data["cpu"]!.AsArray();
            Assert.That(cpu.Select(c => c!["machine"]!.GetValue<string>()), Does.Contain("Bass"));
            Assert.That(Of(steps, "audio_status").Data.ContainsKey("dropouts"), Is.True);
            Assert.That(Of(steps, "settings_get", 0).Data.ToJsonString(), Does.Contain("audio.AudioThreads"));
            Assert.That(Of(steps, "settings_get", 1).Data.ToJsonString(), Does.Contain("advanced.SubTickResolution"));
        });
    }
}
