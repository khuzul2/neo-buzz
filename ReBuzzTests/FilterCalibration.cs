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
    /// with its LFO still (the source says how it should read), Joachims Mars filter 2 at its modes and two input levels
    /// (its documentation calls it a distortion), Pedal Filter where the build has it (a full package run). Then
    /// machine_replace's comparison of each pair, where the build has Neo Filter and the pair's mapping.
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
                Report($"[notice] Filter calibration, {label} (rate {fx.Rate}, R/L {lr:0.00} dB): {FineTransfer.Text(hz, FineTransfer.Db(mono, fx.Left, fx.Rate, hz))}");
            }

            // Jeskola Filter and Filter 2: Type 0 lowpass, 1 highpass, 2 bandpass; Cutoff, Resonance and Bandwidth 0–128.
            foreach (var gear in new[] { "Jeskola Filter", "Jeskola Filter 2" })
            {
                foreach (int c in new[] { 0, 8, 16, 32, 48, 64, 80, 96, 112, 120, 128 })
                    Response($"{gear} lowpass, cutoff {c}, resonance 0", gear, Raw(("Type", 0), ("Cutoff", c), ("Resonance", 0)));
                foreach (int r in new[] { 16, 32, 48, 64, 80, 96, 112, 118 })
                    Response($"{gear} lowpass, cutoff 64, resonance {r}", gear, Raw(("Type", 0), ("Cutoff", 64), ("Resonance", r)));
                foreach (int c in new[] { 32, 96 })
                    foreach (int r in new[] { 64, 112 })
                        Response($"{gear} lowpass, cutoff {c}, resonance {r}", gear, Raw(("Type", 0), ("Cutoff", c), ("Resonance", r)));
                foreach (int c in new[] { 16, 48, 80, 112 })
                    Response($"{gear} highpass, cutoff {c}, resonance 0", gear, Raw(("Type", 1), ("Cutoff", c), ("Resonance", 0)));
                Response($"{gear} highpass, cutoff 64, resonance 64", gear, Raw(("Type", 1), ("Cutoff", 64), ("Resonance", 64)));
                foreach (int c in new[] { 32, 64, 96 })
                    Response($"{gear} bandpass, cutoff {c}, resonance 0, bandwidth 0", gear, Raw(("Type", 2), ("Cutoff", c), ("Resonance", 0), ("Bandwidth", 0)));
                foreach (int b in new[] { 32, 64, 96, 128 })
                    Response($"{gear} bandpass, cutoff 64, resonance 0, bandwidth {b}", gear, Raw(("Type", 2), ("Cutoff", 64), ("Resonance", 0), ("Bandwidth", b)));
                foreach (int b in new[] { 0, 64 })
                    Response($"{gear} bandpass, cutoff 64, resonance 64, bandwidth {b}", gear, Raw(("Type", 2), ("Cutoff", 64), ("Resonance", 64), ("Bandwidth", b)));
                // Whether its resonance is linear: the same setting 18 dB quieter and 6 dB louder.
                Response($"{gear} lowpass, cutoff 64, resonance 112, input -30 dB", gear, Raw(("Type", 0), ("Cutoff", 64), ("Resonance", 112)), "-30 dB");
                Response($"{gear} lowpass, cutoff 64, resonance 112, input -6 dB", gear, Raw(("Type", 0), ("Cutoff", 64), ("Resonance", 112)), "-6 dB");
            }

            // FSM WahMan with its LFO still (Modulation 0): its cutoff and resonance scales (its source: a biquad low-pass at
            // 264 · 32^(Cutoff/240) Hz, Q from Resonance).
            foreach (int c in new[] { 0, 60, 120, 180, 240 })
                Response($"FSM WahMan cutoff {c}, resonance 120, modulation 0", "FSM WahMan", OnTrack(Raw(("Cutoff", c), ("Resonance", 120), ("Modulation", 0))));
            foreach (int r in new[] { 0, 60, 180, 240 })
                Response($"FSM WahMan cutoff 120, resonance {r}, modulation 0", "FSM WahMan", OnTrack(Raw(("Cutoff", 120), ("Resonance", r), ("Modulation", 0))));

            // Joachims Mars filter 2: bypass, its defaults at three levels, each mode at its defaults, the attenuators on.
            Response("Mars mode 0 (bypass)", "Joachims Mars filter 2", Raw(("Distortion mode", 0)));
            foreach (var level in new[] { "-36 dB", "-12 dB", "-3 dB" })
                Response($"Mars defaults, input {level}", "Joachims Mars filter 2", null, level);
            for (int mode = 2; mode <= 13; mode++)
                Response($"Mars mode {mode}", "Joachims Mars filter 2", Raw(("Distortion mode", mode)));
            Response("Mars treble behavior 64", "Joachims Mars filter 2", Raw(("Treble behavior", 64)));
            Response("Mars bass behavior 64", "Joachims Mars filter 2", Raw(("Bass behavior", 64)));
            Response("Mars amount 0", "Joachims Mars filter 2", Raw(("Amount", 0)));

            // Pedal Filter (a full package run): the method against its source (TPT SVF, Q = 1 / (2 − 1.99 · Resonance)).
            Response("Pedal Filter low pass 1 kHz, resonance 20", "Pedal Filter", Raw(("Cutoff", 1000), ("Resonance", 20)));
            Response("Pedal Filter low pass 24 dB 1 kHz, resonance 60", "Pedal Filter", Raw(("Slope", 1), ("Cutoff", 1000), ("Resonance", 60)));

            // Neo Filter itself: the method's check (its design is exact), where the build has it.
            Response("Neo Filter defaults", "Neo Filter", null);

            // machine_replace's comparisons, where the build has Neo Filter and the mapping.
            void Compare(string label, string gear, JsonArray changes)
            {
                if (!Present(gear) || !Present("Neo Filter")) return;
                foreach (bool noise in new[] { true, false })
                {
                    EffectSong(client, gear, changes?.DeepClone().AsArray(), noise);
                    var r = client.Request("tools/call", new JsonObject { ["name"] = "machine_replace", ["arguments"] = Args(("machine", "FX")) });
                    if (r["result"]?["isError"]?.GetValue<bool>() != false)
                    {
                        Report($"[notice] Filter calibration, {label}: machine_replace refused: {r["result"]?["content"]?[0]?["text"] ?? r["error"]?.ToJsonString()}");
                        return;
                    }
                    var s = r["result"]!["structuredContent"]!;
                    var c = s["comparison"];
                    var m = c?["machine"] ?? c?["song"];
                    Report($"[notice] Filter calibration, {label}, {(noise ? "noise" : "chords")}: {c?["within_tolerance"]?.ToJsonString() ?? "-"}, {m?["loudness_db"]?["difference"]?.ToJsonString() ?? "-"} dB, " +
                           $"worst {m?["worst_band"]?["difference_db"]?.ToJsonString() ?? "-"} dB at {m?["worst_band"]?["hz"]?.ToJsonString() ?? "-"} Hz; notes {s["notes"]?.ToJsonString() ?? "-"}");
                }
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
