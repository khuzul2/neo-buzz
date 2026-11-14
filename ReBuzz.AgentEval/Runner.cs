// Evaluation: runs checks and tasks against a headless ReBuzz, restarting it if it dies, and
// counts each item's tool calls, error calls and wall time.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    internal sealed class Runner : IDisposable
    {
        readonly string exe;
        readonly bool verbose;
        McpClient client;

        public Runner(string exe, bool verbose)
        {
            this.exe = Path.GetFullPath(exe);
            this.verbose = verbose;
        }

        public string ServerVersion { get; private set; }
        public List<string> Problems { get; } = new();

        /// <summary>Starts ReBuzz in agent mode (optionally on a song) and initializes.</summary>
        public McpClient Start(string song = null)
        {
            var args = new List<string> { "--agent" };
            if (song != null) args.Add(Path.GetFullPath(song));
            var c = McpClient.Start(exe, args);
            if (verbose) c.StderrLine += line => Console.Error.WriteLine("  | " + line);
            var clock = Stopwatch.StartNew();
            var init = c.Initialize();
            ServerVersion ??= $"{J.Str(init["serverInfo"]?["version"])} (protocol {J.Str(init["protocolVersion"])})";
            Log($"{Product.Name} {ServerVersion} answered in {clock.Elapsed.TotalSeconds:F1} s");
            if (verbose) c.CallMade += call => Console.Error.WriteLine($"    {call.Tool} {(call.IsError ? "ERROR " : "")}{call.Seconds:F2}s: {Trim(call.Text, 160)}");
            return c;
        }

        static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;

        public static void Log(string line) => Console.WriteLine($"[eval {DateTime.Now:HH:mm:ss}] {line}");

        /// <summary>The session, started (again) when needed.</summary>
        public Studio Session(string what)
        {
            if (client != null && Alive(client)) return new Studio(client);
            if (client != null)
            {
                Problems.Add($"{Product.Name} stopped answering before {what}; restarted it. Its last words: {Trim(client.Diagnostics(), 600)}");
                client.Dispose();
            }
            client = Start();
            return new Studio(client);
        }

        static bool Alive(McpClient c)
        {
            try
            {
                c.Request("ping", new JsonObject(), 20);
                return true;
            }
            catch (Exception) { return false; }
        }

        public CheckResult RunCheck(Checks.Def def, Checks.Env env)
        {
            var result = new CheckResult { Name = def.Name, Title = def.Title };
            Log($"check {def.Name}");
            var s = Session(def.Name);
            int calls = s.C.Calls, errors = s.C.ErrorCalls;
            var clock = Stopwatch.StartNew();
            try
            {
                def.Run(s, new CheckRun(result), env);
            }
            catch (Exception e)
            {
                result.Failures.Add($"{e.GetType().Name}: {e.Message}");
            }
            result.Seconds = clock.Elapsed.TotalSeconds;
            result.Calls += s.C.Calls - calls;
            result.Errors += s.C.ErrorCalls - errors;
            result.Passed = result.Failures.Count == 0;
            Log($"  {(result.Passed ? "pass" : "FAIL")} in {result.Seconds:F1} s, {result.Calls} calls" + (result.Passed ? "" : ": " + string.Join(" | ", result.Failures.Take(5))));
            return result;
        }

        /// <summary>Setup → the starting song's score → the reference solution → its score.</summary>
        public TaskResult RunTask(EvalTask task, string root)
        {
            var ctx = new TaskContext(task.Name, Path.Combine(root, "tasks", task.Name));
            var result = new TaskResult { Name = task.Name, Title = task.Title, Solver = "reference", Song = ctx.ResultSong };
            Log($"task {task.Name}");
            File.WriteAllText(ctx.File("brief.md"), task.Brief(ctx) + "\n");
            var total = Stopwatch.StartNew();
            Facts start = null;
            try
            {
                var s = Session(task.Name + " setup");
                s.New();
                task.Setup(s, ctx);
                s.Save(ctx.StartSong);
                s.Open(ctx.StartSong);
                start = task.Measure(s, ctx);
                var startCriteria = task.Score(start, start, ctx);
                var startResult = new TaskResult { Criteria = startCriteria };
                result.StartTotal = startResult.Total;
                result.StartPassed = startResult.Passed;
                result.StartFacts = start.ToJson();
                Log($"  start scores {startResult.Total:F2}");

                s = Session(task.Name + " solution");
                s.Open(ctx.StartSong);
                int calls = s.C.Calls, errors = s.C.ErrorCalls;
                var clock = Stopwatch.StartNew();
                task.Solve(s, ctx);
                s.Save(ctx.ResultSong);
                result.Seconds = clock.Elapsed.TotalSeconds;
                result.Calls = s.C.Calls - calls;
                result.Errors = s.C.ErrorCalls - errors;

                s.Open(ctx.ResultSong);
                var facts = task.Measure(s, ctx);
                result.Facts = facts.ToJson();
                result.Criteria = task.Score(start, facts, ctx);
            }
            catch (TaskSkipped e)
            {
                Log($"  skipped: {e.Message}");
                return null;
            }
            catch (Exception e)
            {
                result.Error = $"{e.GetType().Name}: {e.Message}";
            }
            Log($"  {(result.Passed ? "pass" : "FAIL")} {result.Total:F2} ({string.Join(", ", result.Criteria.Select(c => $"{c.Name} {c.Score:F2}"))}) " +
                $"in {total.Elapsed.TotalSeconds:F1} s" + (result.Error != null ? $"; {Trim(result.Error, 500)}" : ""));
            return result;
        }

        /// <summary>Scores a song an agent saved (score mode): the starting song too, for the tasks that compare with it.</summary>
        public TaskResult ScoreSong(EvalTask task, TaskContext ctx, string song)
        {
            var result = new TaskResult { Name = task.Name, Title = task.Title, Solver = "agent", Song = song };
            try
            {
                var s = Session("scoring");
                Facts start;
                if (File.Exists(ctx.StartSong)) s.Open(ctx.StartSong);
                else s.New();
                start = task.Measure(s, ctx);
                result.StartFacts = start.ToJson();
                var startResult = new TaskResult { Criteria = task.Score(start, start, ctx) };
                result.StartTotal = startResult.Total;
                result.StartPassed = startResult.Passed;
                if (!File.Exists(song))
                {
                    result.Error = $"No song at {song}: the agent didn't save one.";
                    result.Criteria = startResult.Criteria.Select(c => new Criterion(c.Name, 0, "no song saved")).ToList();
                    return result;
                }
                s.Open(song);
                var facts = task.Measure(s, ctx);
                result.Facts = facts.ToJson();
                result.Criteria = task.Score(start, facts, ctx);
            }
            catch (Exception e)
            {
                result.Error = $"{e.GetType().Name}: {e.Message}";
            }
            return result;
        }

        /// <summary>Closes the session: ReBuzz must exit when stdin closes.</summary>
        public void Close()
        {
            if (client == null) return;
            if (!client.CloseAndWait(60)) Problems.Add(Product.Name + " didn't exit within 60 s of stdin closing.");
            if (client.ProtocolViolations.Count > 0) Problems.Add("stdout carried non-protocol lines: " + string.Join(" | ", client.ProtocolViolations.Take(3)));
            client.Dispose();
            client = null;
        }

        public void Dispose() => client?.Dispose();
    }
}
