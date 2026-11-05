using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/machines.md, Neo Filter; mcp-server.md §12.10): the classic filters measured for Neo
    /// Filter's successor pairs. Each response is read on held noise from the machine's input and output (FineTransfer, 12
    /// points an octave), so a cutoff's −3 dB point, a resonance's peak and the slopes past them come from the binary, not
    /// from its texts: Jeskola Filter and Filter 2 (closed, bare 0–128 scales) at each type over their scales, FSM WahMan
    /// with its LFO still (the source says how it should read), Joachims Mars filter 2 at its modes and input levels (its
    /// documentation calls it a distortion), Pedal Filter where the build has it (a full package run). Then
    /// machine_replace's comparison of each pair on noise, where the build has Neo Filter and the pair's mapping.
    /// </summary>
    public partial class AgentHeadlessTests
    {
        [Test, Explicit("measurements for Neo Filter's successor pairs; run by name (test_filter) when a mapping is recalibrated")]
        public void FilterCalibration()
        {
            using var client = StartAgent(out string bin);
            void Report(string line) => TestContext.Out.WriteLine(line);   // as it comes: a failure later keeps what was measured
            WarmUp(client, "Jeskola Filter");
            var hz = FineTransfer.Frequencies(20, 20000, 12);
            bool Present(string gear) => Has(bin, "Effects", gear) || Has(bin, "Effects", gear + ".NET");
            void Response(string label, string gear, JsonArray changes, string volume = "-12 dB")
            {
                if (!Present(gear)) { Report($"[notice] {gear} not in this build's Gear"); return; }
                HeldNoise(client, gear, changes);
                Quiet(client, "param_set", Args(("machine", "Synth"), ("changes", Changes(("Volume", volume)))));
                var render = Quiet(client, "render", Args(("tail_seconds", 0), ("probes", new JsonArray("FX", "Synth"))));
                var probes = render["probes"]!.AsArray();
                string ProbeFile(string m) => probes.First(p => p!["machine"]!.GetValue<string>() == m)!["file"]!.GetValue<string>();
                var fx = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("FX"));
                var src = ReBuzz.Agent.Audio.Wav.Read(ProbeFile("Synth"));
                var mono = src.Left.Zip(src.Right ?? src.Left, (l, r) => (l + r) / 2).ToArray();
                double lr = fx.Right == null ? 0 : FineTransfer.Db(fx.Left, fx.Right, fx.Rate, new[] { 1000.0 })[0];
                var db = FineTransfer.Db(mono, fx.Left, fx.Rate, hz);
                Report($"[notice] Filter calibration, {label} (rate {fx.Rate}, R/L {lr:0.00} dB): {string.Join(" ", db.Select(d => Math.Round(d * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
            }

            // Each line has the response's values alone, in 0.1 dB, at 20 Hz · 2^(i/12) up to 20 kHz: the CI summary keeps 60 lines and
            // the last 40 kB of a test's output, so what was measured before stays out (the runs named), and the rest is what's left.
            //   Run 41981112935: Jeskola Filter and Filter 2 are each a Chamberlin state-variable filter, f and damping q = 1 − sin(π ·
            //   Resonance / 256) from the raw scales; Jeskola Filter's f = sin(π · Cutoff / 256).
            //   Run 41983622777: Filter 2's cutoff scale every 4 steps at resonance 0; Jeskola Filter's top of the scale in each type.
            //   Run 41988788419: Joachims Mars filter 2's modes 3–13 and attenuators, Pedal Filter, Neo Filter's defaults.
            // Here: Filter 2's odd steps between two of its measured ones, its top of the scale in each type, both filters high in
            // resonance, Jeskola Filter's Bandwidth in band-pass, FSM WahMan with its LFO still, and Mars's bypass, defaults and mode 2.
            foreach (int c in new[] { 33, 34, 35, 97, 98, 99 })
                Response($"Jeskola Filter 2 lowpass, cutoff {c}, resonance 0", "Jeskola Filter 2", Raw(("Type", 0), ("Cutoff", c), ("Resonance", 0)));
            foreach (int c in new[] { 124, 126, 127 })
                Response($"Jeskola Filter 2 lowpass, cutoff {c}, resonance 0", "Jeskola Filter 2", Raw(("Type", 0), ("Cutoff", c), ("Resonance", 0)));
            foreach (int c in new[] { 120, 128 })
                foreach (int type in new[] { 1, 2 })
                    Response($"Jeskola Filter 2 {(type == 1 ? "highpass" : "bandpass")}, cutoff {c}, resonance 0", "Jeskola Filter 2", Raw(("Type", type), ("Cutoff", c), ("Resonance", 0)));
            Response("Jeskola Filter 2 bandpass, cutoff 64, resonance 64", "Jeskola Filter 2", Raw(("Type", 2), ("Cutoff", 64), ("Resonance", 64)));
            foreach (var gear in new[] { "Jeskola Filter", "Jeskola Filter 2" })
                Response($"{gear} lowpass, cutoff 40, resonance 116", gear, Raw(("Type", 0), ("Cutoff", 40), ("Resonance", 116)));
            foreach (int r in new[] { 0, 64 })
                foreach (int b in new[] { 4, 8, 16, 24, 32, 40, 48, 56 })
                    Response($"Jeskola Filter bandpass, cutoff 64, resonance {r}, bandwidth {b}", "Jeskola Filter", Raw(("Type", 2), ("Cutoff", 64), ("Resonance", r), ("Bandwidth", b)));
            foreach (int c in new[] { 16, 96 })
                Response($"Jeskola Filter bandpass, cutoff {c}, resonance 0, bandwidth 64", "Jeskola Filter", Raw(("Type", 2), ("Cutoff", c), ("Resonance", 0), ("Bandwidth", 64)));
            Response("Jeskola Filter lowpass, cutoff 64, resonance 0, bandwidth 64", "Jeskola Filter", Raw(("Type", 0), ("Cutoff", 64), ("Resonance", 0), ("Bandwidth", 64)));

            // FSM WahMan with its LFO still (Modulation 0): its source's biquad low-pass at 264 · 32^(Cutoff/240) Hz, Q from Resonance.
            foreach (int c in new[] { 0, 120, 240 })
                Response($"FSM WahMan cutoff {c}, resonance 120, modulation 0", "FSM WahMan", OnTrack(Raw(("Cutoff", c), ("Resonance", 120), ("Modulation", 0))));
            foreach (int r in new[] { 0, 240 })
                Response($"FSM WahMan cutoff 120, resonance {r}, modulation 0", "FSM WahMan", OnTrack(Raw(("Cutoff", 120), ("Resonance", r), ("Modulation", 0))));

            // Joachims Mars filter 2: bypass, its defaults at three levels, mode 2 (its documentation calls it a distortion).
            Response("Mars mode 0 (bypass)", "Joachims Mars filter 2", Raw(("Distortion mode", 0)));
            foreach (var level in new[] { "-36 dB", "-12 dB", "-3 dB" })
                Response($"Mars defaults, input {level}", "Joachims Mars filter 2", null, level);
            Response("Mars mode 2", "Joachims Mars filter 2", Raw(("Distortion mode", 2)));

            // machine_replace's comparisons on noise (AgentReplaceTests has the pairs' figures), where the build has Neo Filter and the mapping.
            void Compare(string label, string gear, JsonArray changes)
            {
                if (!Present(gear) || !Present("Neo Filter")) return;
                EffectSong(client, gear, changes, noise: true);
                var r = client.Request("tools/call", new JsonObject { ["name"] = "machine_replace", ["arguments"] = Args(("machine", "FX")) });
                if (r["result"]?["isError"]?.GetValue<bool>() != false)
                {
                    Report($"[notice] Filter calibration, {label}: machine_replace refused: {r["result"]?["content"]?[0]?["text"] ?? r["error"]?.ToJsonString()}");
                    return;
                }
                var s = r["result"]!["structuredContent"]!;
                var c = s["comparison"];
                var m = c?["machine"] ?? c?["song"];
                Report($"[notice] Filter calibration, {label}, noise: {c?["within_tolerance"]?.ToJsonString() ?? "-"}, {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                       $"worst {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz; notes {s["notes"]?.ToJsonString() ?? "-"}");
            }
            Compare("Jeskola Filter defaults", "Jeskola Filter", null);
            Compare("Jeskola Filter lowpass 64, resonance 64", "Jeskola Filter", Raw(("Cutoff", 64), ("Resonance", 64)));
            Compare("Jeskola Filter highpass 48", "Jeskola Filter", Raw(("Type", 1), ("Cutoff", 48)));
            Compare("Jeskola Filter bandpass 64, bandwidth 64", "Jeskola Filter", Raw(("Type", 2), ("Cutoff", 64), ("Bandwidth", 64)));
            Compare("Jeskola Filter 2 lowpass 64, resonance 64", "Jeskola Filter 2", Raw(("Cutoff", 64), ("Resonance", 64)));
            Compare("Jeskola Filter 2 highpass 48", "Jeskola Filter 2", Raw(("Type", 1), ("Cutoff", 48)));
            Compare("FSM WahMan defaults", "FSM WahMan", null);
            Compare("FSM WahMan cutoff 60, resonance 200, LFO still", "FSM WahMan", OnTrack(Raw(("Cutoff", 60), ("Resonance", 200), ("Modulation", 0))));
            Compare("Pedal Filter defaults", "Pedal Filter", null);
            Compare("Pedal Filter high pass 24 dB 500 Hz, resonance 60", "Pedal Filter", Raw(("Mode", 1), ("Slope", 1), ("Cutoff", 500), ("Resonance", 60)));
            Assert.That(client.CloseAndWait(60), Is.True, client.Errors);
        }
    }
}
