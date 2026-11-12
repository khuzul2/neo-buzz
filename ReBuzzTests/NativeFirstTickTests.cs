using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (follow-ups): native machines set up before the engine's first tick in a process. The master info a
    /// native machine is sent (tempo, sample rate, samples a tick) was copied by each tick into what the messages carry, so
    /// until the first tick it was all zeros, and a machine's Init and AttributesChanged ran at 0 Hz and 0 BPM: Jeskola Delay's
    /// and Cross Delay's lines were 0 samples long and their first Work looped for ever (their host "stopped working" after
    /// 2 s: the UI gallery's Jeskola Delay, 12–14 s after the demo song was built, at the first offline play; an agent's first
    /// song with them), and FSM Infectors played silence in a process's first song (four of the Infector demo's five in run
    /// 42184661818). Neither app has an audio device on the runner, so nothing ticks before a song is opened; with a device
    /// the audio thread ticks from the start, which is why it never showed there. Init now gets the engine's own master info
    /// before the first tick (WorkManager.CopyEngineMasterInfo).
    /// </summary>
    public partial class AgentHeadlessTests
    {
        /// <summary>The Infector demo, opened first thing in a new agent process: every native in it sounds and none stops answering.</summary>
        [Test]
        public void NativeMachinesSetUpBeforeTheFirstTickPlayTheFirstRender()
        {
            string song = Path.Combine(Repo(), "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx");
            if (!File.Exists(song)) Assert.Inconclusive($"{song} not found");
            using var client = StartAgent(out string bin);
            if (!Has(bin, "Generators", "FSM Infector") || !Has(bin, "Effects", "Jeskola Cross Delay") || !Has(bin, "Effects", "FSM PanzerDelay"))
                Assert.Inconclusive("FSM Infector, Jeskola Cross Delay or FSM PanzerDelay not in this build's Gear");
            Quiet(client, "song_open", Args(("path", song), ("discard_unsaved", true)));
            string[] machines = { "Infector", "Infector2", "Infector3", "Infector4", "Infector5", "X-Delay", "X-Delay2", "PanzDelay" };
            var render = Quiet(client, "render", Args(("tail_seconds", 1), ("probes", new JsonArray(machines.Select(m => (JsonNode)m).ToArray()))));
            var peaks = render["probes"]!.AsArray().ToDictionary(p => p!["machine"]!.GetValue<string>(),
                p => p!["measure"]?["peaks"]?["sample_peak_dbfs"] is JsonValue v && v.TryGetValue(out double d) ? d : double.NegativeInfinity);
            TestContext.Out.WriteLine("[notice] The Infector demo, a process's first render: " + string.Join(", ", machines.Select(m => $"{m} {(peaks.TryGetValue(m, out double p) ? p.ToString("0.0") : "-")} dBFS")));
            var health = Quiet(client, "machine_list")["machines"]!.AsArray()
                .Where(m => m!["health"] is JsonValue h && h.GetValue<string>() == "crashed").Select(m => $"{m!["name"]}: {m["health"]}").ToList();
            Assert.That(health, Is.Empty, "no machine crashed");
            Assert.That(client.Said("native host hang"), Is.False, $"no host stopped answering:\n{client.Errors}");
            foreach (var m in machines)
                Assert.That(peaks.TryGetValue(m, out double p) ? p : double.NegativeInfinity, Is.GreaterThan(-60.0), $"{m} sounds in the process's first render");
        }
    }
}
