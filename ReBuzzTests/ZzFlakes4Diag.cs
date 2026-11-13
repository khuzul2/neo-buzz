using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzzTests
{
    // Temporary (fix/ci-flakes-4): how often does FSM WahMan -> Neo Filter miss with upstream's WahMan, in one process and across processes.
    public partial class AgentHeadlessTests
    {
        [Test]
        public void ZzWahManRepeat()
        {
            int misses = 0, total = 0;
            var cases = new (string Label, JsonArray Changes)[]
            {
                ("cutoff 60, resonance 200, LFO still", OnTrack(Raw(("Cutoff", 60), ("Resonance", 200), ("Modulation", 0)))),
                ("cutoff 200, resonance 0, LFO still", OnTrack(Raw(("Cutoff", 200), ("Resonance", 0), ("Modulation", 0)))),
            };
            for (int process = 0; process < 4; process++)
            {
                using var client = StartAgent(out string bin);
                if (!Has(bin, "Effects", "FSM WahMan") || !File.Exists(Path.Combine(bin, "Gear", "Effects", "Neo Filter.NET.dll")))
                    Assert.Inconclusive("FSM WahMan or Neo Filter not in this build's Gear");
                var line = new System.Text.StringBuilder();
                for (int i = 0; i < 8; i++)
                    foreach (var (label, changes) in cases)
                    {
                        EffectSong(client, "FSM WahMan", changes.DeepClone().AsArray(), noise: true);
                        var w = Within(Replace(client, $"diag p{process} #{i} {label}"));
                        total++;
                        if (w != true) misses++;
                        line.Append(w == true ? '.' : 'X');
                    }
                TestContext.Out.WriteLine($"[notice] WahMan repeat process {process}: {line}");
                Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
            }
            TestContext.Out.WriteLine($"[notice] WahMan repeat: {misses} misses of {total}");
        }
    }
}
