// Evaluation: checks of the evaluation itself that need no ReBuzz (Linux CI, Fork CI's
// session-tests job): the MCP client against an in-process server of our protocol layer, and the
// meters and scoring rules on synthetic signals with known answers, so a scorer that can't tell a
// fixed mix from a broken one fails here before it misleads a run.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.AgentEval
{
    internal static class SelfTest
    {
        static int failures, passes;

        static void Expect(bool ok, string what)
        {
            if (ok) passes++;
            else
            {
                failures++;
                Console.WriteLine("FAIL: " + what);
            }
        }

        public static int Run()
        {
            Client();
            Signals();
            Scoring();
            GateRules();
            Console.WriteLine($"selftest: {passes} passed, {failures} failed");
            return failures == 0 ? 0 : 1;
        }

        // ---- the client against our own server ---------------------------------------------------

        static void Client()
        {
            var tools = new ToolRegistry();
            tools.Add(new ToolDef
            {
                Name = "echo_back",
                Title = "Echo",
                Description = "Returns what it got. Example: echo_back(text=\"hi\").",
                Input = Schema.Object(null, Schema.Req("text", Schema.String("Text"))),
                Output = Schema.Object(null, Schema.Req("text", Schema.String("Text"))),
                Annotations = ToolAnnotations.Read,
                Handler = c => Task.FromResult(ToolResult.Ok("Echo: " + c.RequiredString("text"), new JsonObject { ["text"] = c.RequiredString("text") })),
            });
            tools.Add(new ToolDef
            {
                Name = "always_fail",
                Title = "Fail",
                Description = "Fails. Example: always_fail().",
                Input = Schema.Object(null),
                Handler = c => throw new ToolError("It failed; try echo_back."),
            });
            var server = new McpServer(new ServerInfo { Name = "selftest", Title = "Self-test", Version = "1", Instructions = "Test." }, tools);
            var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
            var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
            var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
            var serverOut = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);
            var serving = Task.Run(() => new StdioTransport(server, serverIn, serverOut).RunAsync());
            using var client = new McpClient(fromServer, toServer);
            var init = client.Initialize(10);
            Expect(J.Str(init["serverInfo"]?["name"]) == "selftest", "initialize returns serverInfo");
            Expect(client.Instructions == "Test.", "initialize returns instructions");
            var list = client.ListTools();
            Expect(list.Count == 2, $"tools/list lists 2 tools, got {list.Count}");
            var ok = client.Call("echo_back", new JsonObject { ["text"] = "héllo" });
            Expect(!ok.IsError && J.Str(ok.Data["text"]) == "héllo" && ok.Text == "Echo: héllo", $"a call returns structured content: {ok.Text}");
            var bad = client.Call("always_fail");
            Expect(bad.IsError && bad.Text.Contains("try echo_back"), $"isError comes back with the text: {bad.Text}");
            var invalid = client.Call("echo_back", new JsonObject { ["txt"] = "x" });
            Expect(invalid.IsError && invalid.Text.Contains("text"), $"invalid arguments are a tool error: {invalid.Text}");
            Expect(client.Calls == 3 && client.ErrorCalls == 2, $"calls counted: {client.Calls}, errors {client.ErrorCalls}");
            try
            {
                client.Call("echo_bak");
                Expect(false, "an unknown tool throws McpException");
            }
            catch (McpException e) { Expect(e.Message.Contains("Did you mean"), $"an unknown tool's error says what next: {e.Message}"); }
            Expect(client.ProtocolViolations.Count == 0, "no protocol violations");
            client.CloseAndWait(1);
            Expect(serving.Wait(TimeSpan.FromSeconds(10)), "the server ends when its input closes");
        }

        // ---- meters on known signals ---------------------------------------------------------------

        const int Rate = 48000;

        static StereoAudio Make(double seconds, Func<double, (double L, double R)> f)
        {
            int n = (int)(seconds * Rate);
            var l = new float[n];
            var r = new float[n];
            for (int i = 0; i < n; i++)
            {
                var (a, b) = f(i / (double)Rate);
                l[i] = (float)a;
                r[i] = (float)b;
            }
            return new StereoAudio { Left = l, Right = r, Rate = Rate };
        }

        static StereoAudio Mono(double seconds, Func<double, double> f) => Make(seconds, t => { double v = f(t); return (v, v); });

        static double Saw(double t, double hz, double cutoffHz = 8000)
        {
            double v = 0;
            for (int h = 1; h * hz < cutoffHz; h++) v += Math.Sin(2 * Math.PI * hz * h * t) / h;
            return 0.3 * v;
        }

        /// <summary>A kick-like hit on each beat: a 55 Hz sine decaying over 250 ms.</summary>
        static double KickAt(double t, double beat) { double p = t % beat; return 0.7 * Math.Exp(-p / 0.08) * Math.Sin(2 * Math.PI * 55 * p); }

        static void Signals()
        {
            // Spectral distance: zero against itself, large against a darker sound.
            var bright = Mono(2, t => Saw(t, 110));
            var dark = Mono(2, t => Saw(t, 110, 900));
            var sb = Meters.Spectrum(bright);
            Expect(Meters.SpectralDistance(sb, sb).Distance < 1e-9, "spectral distance to itself is 0");
            double d = Meters.SpectralDistance(sb, Meters.Spectrum(dark)).Distance;
            Expect(d > 6, $"a saw filtered at 900 Hz is far from the full saw: {d:F2} dB");
            var quieter = Mono(2, t => 0.25 * Saw(t, 110));
            Expect(Meters.SpectralDistance(sb, Meters.Spectrum(quieter)).Distance < 0.05, "spectral distance is level-matched");

            // Envelopes: a pluck against a held note.
            var pluck = Mono(3, t => Math.Exp(-(t % 0.5) / 0.08) * Saw(t, 220));
            var held = Mono(3, t => 0.5 * Saw(t, 220));
            Expect(Meters.EnvelopeDistance(pluck, pluck) < 1e-9, "envelope distance to itself is 0");
            double e = Meters.EnvelopeDistance(pluck, held);
            Expect(e > 8, $"a held note is far from a pluck's envelope: {e:F2} dB");

            // Masking: a kick and a bass on 55 Hz fight; the bass an octave and more higher, much less.
            double beat = 60 / 120.0;
            var kick = Mono(4, t => KickAt(t, beat));
            var bass = Mono(4, t => 0.35 * Math.Sin(2 * Math.PI * 55 * t));
            var bassHigh = Mono(4, t => 0.2 * Saw(t, 220, 2000));
            var fight = Meters.Masking(kick, bass);
            var apart = Meters.Masking(kick, bassHigh);
            Expect(fight.Sum > 0.15, $"kick and 55 Hz bass fight: {fight.Sum:F2} ({fight.Where})");
            Expect(apart.Sum < 0.5 * fight.Sum, $"a higher bass fights less: {apart.Sum:F2} against {fight.Sum:F2}");

            // Resonance: a saw with a +18 dB peak at 3.15 kHz stands out; the plain saw doesn't.
            var resonant = Mono(2, t => Saw(t, 110, 12000) + 0.25 * Math.Sin(2 * Math.PI * 3150 * t));
            double peaked = Meters.Prominence(Meters.Spectrum(resonant), 3150), plain = Meters.Prominence(Meters.Spectrum(Mono(2, t => Saw(t, 110, 12000))), 3150);
            Expect(peaked > 8 && plain < 3, $"resonance prominence: {peaked:F1} dB with the peak, {plain:F1} dB without");

            // Phase: anti-phase sides read negative; a narrowed version positive.
            var rng = new Random(3);
            var noise = Enumerable.Range(0, 2 * Rate).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
            var rng2 = new Random(4);
            var noise2 = Enumerable.Range(0, 2 * Rate).Select(_ => rng2.NextDouble() * 2 - 1).ToArray();
            StereoAudio Wide(double width) => Make(2, t =>
            {
                int i = Math.Min(noise.Length - 1, (int)(t * Rate));
                double mid = 0.2 * noise[i], side = 0.2 * noise2[i] * width;
                return (mid + side, mid - side);
            });
            double wide = Meters.Stereo(Wide(1.5)).Correlation, narrow = Meters.Stereo(Wide(0.7)).Correlation;
            Expect(wide < -0.3 && narrow > 0.1, $"150 % width correlates {wide:F2}, 70 % {narrow:F2}");

            // Gaps: a second of silence in the middle.
            var gap = Mono(4, t => t is > 1.5 and < 2.5 ? 0 : 0.3 * Math.Sin(2 * Math.PI * 440 * t));
            double g = Meters.LongestGap(gap);
            Expect(Math.Abs(g - 1) < 0.11, $"the longest gap is 1 s: {g:F2}");
            Expect(Meters.LongestGap(held) < 0.06, "a held note has no gap");

            // WAV round trip through ReBuzz.Agent's reader.
            string file = Path.Combine(Path.GetTempPath(), "rebuzz-agenteval-" + Guid.NewGuid().ToString("N") + ".wav");
            var x = new float[2 * held.Frames];
            for (int i = 0; i < held.Frames; i++) { x[2 * i] = held.Left[i]; x[2 * i + 1] = held.Right[i]; }
            Wav.WriteStereoFloat(file, x, held.Frames, Rate);
            var back = Meters.Read(file);
            Expect(back.Frames == held.Frames && back.Left.SequenceEqual(held.Left), "WAV files read back exactly");
            File.Delete(file);
        }

        // ---- scoring rules -----------------------------------------------------------------------

        static void Scoring()
        {
            Expect(EvalTask.Linear(5, 1, 6) == 0.2 && EvalTask.Linear(0.5, 1, 6) == 1 && EvalTask.Linear(7, 1, 6) == 0, "Linear, falling");
            Expect(EvalTask.Linear(0.9, 0.9, 0.4) == 1 && Math.Abs(EvalTask.Linear(0.65, 0.9, 0.4) - 0.5) < 1e-9, "Linear, rising");
            Expect(EvalTask.Band(10, 6, 14, 3) == 1 && Math.Abs(EvalTask.Band(15.5, 6, 14, 3) - 0.5) < 1e-9 && EvalTask.Band(2, 6, 14, 3) == 0, "Band");
            var aminor = new[] { (45, 1.0), (48, 1.0), (52, 1.0), (43, 1.0), (50, 1.0) };
            Expect(EvalTask.ScaleFit(aminor).Share == 1, "A minor notes fit a scale fully");
            var chromatic = Enumerable.Range(48, 12).Select(p => (p, 1.0));
            Expect(EvalTask.ScaleFit(chromatic).Share < 0.65, "a chromatic run doesn't");

            // Every task's scorer: the starting song's facts can't pass against themselves (empty facts score 0).
            var empty = new Facts();
            foreach (var t in EvalTask.All)
            {
                var ctx = new TaskContext(t.Name, Path.Combine(Path.GetTempPath(), "rebuzz-agenteval-selftest", t.Name));
                var result = new TaskResult { Criteria = t.Score(empty, empty, ctx) };
                Expect(result.Criteria.Count >= 4 && !result.Passed, $"{t.Name}: an empty song fails ({result.Total:F2})");
                Expect(t.Brief(ctx).Contains("song_save(path=") && t.Brief(ctx).Contains("result.bmxml"), $"{t.Name}: the brief says where to save");
            }
        }

        static void GateRules()
        {
            var baseline = JsonNode.Parse("""{"tolerance":0.05,"checks":{"a":true,"b":false},"tasks":{"t":{"total":0.95,"passed":true},"u":{"total":0.5,"passed":false}}}""") as JsonObject;
            EvalRun RunWith(bool a, bool b, double t, double u)
            {
                var run = new EvalRun();
                run.Checks.Add(new CheckResult { Name = "a", Passed = a });
                run.Checks.Add(new CheckResult { Name = "b", Passed = b });
                run.Tasks.Add(new TaskResult { Name = "t", Criteria = { new Criterion("x", t, "") } });
                run.Tasks.Add(new TaskResult { Name = "u", Criteria = { new Criterion("x", u, "") } });
                return run;
            }
            Expect(Gate.Check(RunWith(true, false, 0.95, 0.5), baseline, 0.05).Blocking.Count == 0, "the baseline itself passes the gate");
            Expect(Gate.Check(RunWith(false, false, 0.95, 0.5), baseline, 0.05).Blocking.Count == 1, "a check that passed and fails blocks");
            Expect(Gate.Check(RunWith(true, false, 0.88, 0.5), baseline, 0.05).Blocking.Count == 1, "a task dropping by more than the tolerance blocks");
            Expect(Gate.Check(RunWith(true, false, 0.92, 0.5), baseline, 0.05).Blocking.Count == 0, "a drop within the tolerance doesn't");
            Expect(Gate.Check(RunWith(true, false, 0.95, 0.47), baseline, 0.05).Blocking.Count == 0, "a known failure that stays doesn't block");
            var extra = RunWith(true, false, 0.95, 0.5);
            extra.Checks.Add(new CheckResult { Name = "new", Passed = false, Failures = { "x" } });
            Expect(Gate.Check(extra, baseline, 0.05).Blocking.Count == 1, "a new failing check blocks");
            var startPasses = RunWith(true, false, 0.95, 0.5);
            startPasses.Tasks[0].StartPassed = true;
            Expect(Gate.Check(startPasses, baseline, 0.05).Blocking.Count == 1, "a starting song that passes blocks");
        }
    }
}
