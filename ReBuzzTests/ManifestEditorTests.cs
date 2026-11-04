using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor in ReBuzz.exe, for every machine
    /// with a curated manifest in its Gear (the natives load only there; the Pedal machines too in a full build). U4's
    /// exit check, ReBuzz.exe's half: <c>ReBuzz.exe --agent --editor-shots</c> opens each machine as a double click does
    /// (its manifest editor, or "Editor..." for one with a GUI of its own) through the WPF host, draws it (screenshots
    /// beside SessionViewTests', manifest-editor-*.png) and reads it back: every parameter the agent server shows has its
    /// control with the server's text, a typed value lands where param_set puts it, Jeskola Delay's safe range is
    /// marked, and only known designs get a response curve. The presets: tools/preset-check.py in the same job.
    /// </summary>
    public class ManifestEditorTests
    {
        /// <summary>The curated natives (mcp-server.md §12.9); the quick package has them all.</summary>
        static readonly string[] Natives =
        {
            "FSM Infector", "FSM KickXP", "Oomek Aggressor 303", "Jeskola Bass 3", "Rout 909", "Jeskola Reverb", "Jeskola Delay",
            "Jeskola Cross Delay", "Jeskola Distortion", "Jeskola Filter", "Jeskola EQ-3", "Oomek Exciter", "Oomek Masterizer",
            "FSM PanzerDelay", "Joachims Mars filter 2",
        };

        [Test]
        public void EveryCuratedMachineOpensItsManifestEditorInReBuzz()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, "ReBuzz.exe");
            if (!File.Exists(exe)) Assert.Inconclusive($"ReBuzz.exe not built ({exe})");
            string folder = Path.Combine(Path.GetTempPath(), "rebuzz-editor-shots-" + Environment.ProcessId);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);

            var psi = new ProcessStartInfo(exe, $"--agent --editor-shots \"{folder}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = bin,
            };
            using var process = Process.Start(psi)!;
            var stderr = new List<string>();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.Add(e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (!process.WaitForExit(TimeSpan.FromMinutes(6)))
            {
                process.Kill(true);
                Assert.Fail("ReBuzz --editor-shots didn't finish in 6 minutes:\n" + string.Join("\n", stderr.TakeLast(40)));
            }
            string log = string.Join("\n", stderr);
            TestContext.Out.WriteLine(log);
            string json = Path.Combine(folder, "editors.json");
            Assert.That(File.Exists(json), Is.True, "editors.json written:\n" + log);
            var results = JsonNode.Parse(File.ReadAllText(json))!.AsArray().Select(r => r!.AsObject()).ToDictionary(r => r["gear"]!.GetValue<string>());

            // Screenshots where the tests' others go (published with the run).
            string shots = Environment.GetEnvironmentVariable("SESSION_SCREENSHOT_DIR") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(shots);
            foreach (var png in Directory.GetFiles(folder, "*.png")) File.Copy(png, Path.Combine(shots, Path.GetFileName(png)), true);

            Assert.That(Natives.Where(g => !results.ContainsKey(g)), Is.Empty, "every curated native in Gear was opened");
            Assert.Multiple(() =>
            {
                foreach (var (gear, r) in results)
                {
                    string Say(string what) => $"{gear}: {what} ({r.ToJsonString()})";
                    Assert.That(r["error"], Is.Null, Say("no error"));
                    if (r["error"] != null) continue;
                    TestContext.Out.WriteLine($"[notice] {gear}: opened by {r["opened_by"]}, {r["sections"]} sections, {r["controls"]} controls, " +
                                              $"response {r["response"]}, {r["envelopes"]} envelopes, {r["presets"]} presets, {r["width"]}×{r["height"]}");
                    Assert.That(r["quality"]!.GetValue<string>(), Is.EqualTo("curated"), Say("curated"));
                    Assert.That(r["editor_error"]!.GetValue<bool>(), Is.False, Say("the body built"));
                    Assert.That(r["controls"]!.GetValue<int>(), Is.GreaterThan(0), Say("controls"));
                    Assert.That(r["preset_bar"]!.GetValue<bool>(), Is.True, Say("the preset bar"));
                    Assert.That(r["mismatches"]!.AsArray(), Is.Empty, Say("every control shows the agent server's value"));
                    Assert.That(r["png"], Is.Not.Null, Say("drawn"));
                    if (r["typed"] is JsonObject typed)
                    {
                        Assert.That(typed["applied"]!.GetValue<bool>(), Is.True, Say("a typed value applied"));
                        Assert.That(typed["raw"]!.GetValue<int>(), Is.EqualTo(typed["param_set_raw"]!.GetValue<int>()), Say("where param_set puts it"));
                    }
                    // A machine without a GUI of its own opens its editor on a double click (on by default: curated).
                    if (!r["own_double_click"]!.GetValue<bool>() && r["double_click_opened"] == null)
                        Assert.That(r["opened_by"]!.GetValue<string>(), Is.EqualTo("double click"), Say("a double click opens it"));
                }
                if (results.TryGetValue("Jeskola Delay", out var delay))
                    Assert.That(delay["safe_ranges"]!.AsArray().Select(s => s!["marked"]!.GetValue<bool>()), Is.EqualTo(new[] { true }),
                        "Jeskola Delay's Feedback safe range (from -98.4%) is marked on its knob");
                foreach (var effect in new[] { "Jeskola EQ-3", "FSM PanzerDelay", "Oomek Exciter" })
                    if (results.TryGetValue(effect, out var r))
                        Assert.That(r["response"]?.GetValue<string>(), Is.EqualTo("markers"), $"{effect}: frequencies marked, no curve (no known design)");
                foreach (var synth in new[] { "FSM Infector", "Jeskola Bass 3", "Oomek Aggressor 303" })
                    if (results.TryGetValue(synth, out var r))
                        Assert.That(r["response"]?.GetValue<string>(), Is.EqualTo("none"), $"{synth}: no plot for a synth's filter");
                if (results.TryGetValue("FSM Infector", out var infector))
                    Assert.That(infector["envelopes"]!.GetValue<int>(), Is.EqualTo(2), "FSM Infector: its filter and amp envelopes pictured");
                foreach (var gear in new[] { "Jeskola Delay", "Jeskola Reverb", "FSM Infector", "Joachims Mars filter 2" })
                    if (results.TryGetValue(gear, out var r))
                        Assert.That(r["presets"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(4), $"{gear}: its factory presets beside it");
            });
        }
    }
}
