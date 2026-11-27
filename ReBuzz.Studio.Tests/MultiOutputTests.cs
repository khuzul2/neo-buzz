// Fork (docs/specs/multi-output.md, MO1 #228): machines with several outputs, end to end, against the real engine with the tests' Multi Test
// machine (TestMachines/MultiOut: three named outputs, Low, Mid and High, each a sine of its own pitch). The engine's gates: each chain
// renders from its own output and the mix is their sum to the last bit; 0 bytes on the audio thread, the first block after a new wire from
// output 3 included; the source output saved in .nbl and loaded back, an older song on output 1; undo and redo of connect and the picker;
// the agent's connect with from_output and machine_swap moving wires; and the mixer's strips for the extra outputs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Tests
{
    public class MultiOutputTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static SongCore Song => Core.SongCore;

        static void NewSong() => AudioHygieneTests.NewSong();

        static void Pump() => AudioHygieneTests.Pump();

        static MachineCore Add(string gear, string name)
        {
            Song.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            return Song.MachinesList.Last(x => x.Name == name);
        }

        static MachineCore Master => Song.MachinesList[0];

        /// <summary>A wire from one of the source's outputs (0-based) to the destination's first input, at 0 dB.</summary>
        static IMachineConnection Wire(IMachine from, IMachine to, int output)
        {
            Song.ConnectMachines(from, to, new[] { output }, new[] { 0 }, 0x4000, 0x4000);
            return from.Outputs.Single(o => o.Destination == to);
        }

        /// <summary>Multi Test with a Neo Utility on each output wired to Master, the first <paramref name="chains"/> of them.</summary>
        static (MachineCore Multi, MachineCore[] Chain) ThreeChains(int mask = 7)
        {
            var multi = Add("Multi Test", "Multi");
            var chain = new MachineCore[3];
            for (int k = 0; k < 3; k++) chain[k] = Add("Neo Utility", "Chain " + (k + 1));
            for (int k = 0; k < 3; k++)
            {
                if ((mask & (1 << k)) != 0) Wire(multi, chain[k], k);
                Wire(chain[k], Master, 0);
            }
            Pump();
            return (multi, chain);
        }

        /// <summary>The engine's output, left channel, as the device's thread reads it (256 frames a buffer).</summary>
        static float[] Render(double seconds)
        {
            // As the agent's render starts (RenderTools): on a tick boundary, wherever the engine's tick phase was left by the last song.
            lock (ReBuzzCore.AudioLock)
            {
                ReBuzzCore.masterInfo.PosInTick = 0;
                var sub = ReBuzzCore.subTickInfo;
                sub.PosInSubTick = 0;
                sub.CurrentSubTick = 0;
                sub.SubTickReminderCounter = 0;
            }
            Core.MasterVolume = 1;      // levels as the machines make them (other tests leave it lower)
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            int frames = (int)Math.Round(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256) * 256;
            var y = new float[frames];
            for (int f = 0; f < frames; f += 256)
            {
                work.MainAudioFillBuffer(buffer, 0, 512);
                for (int i = 0; i < 256; i++) y[f + i] = buffer[2 * i];
            }
            work.Stop();
            return y;
        }

        static double HzOf(float[] x, double seconds)
        {
            int crossings = 0;
            for (int i = 1; i < x.Length; i++) if (x[i - 1] < 0 && x[i] >= 0) crossings++;
            return crossings / seconds;
        }

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static JsonObject Call(string tool, JsonObject args = null)
        {
            var r = Try(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            return r.Data;
        }

        static JsonObject Args(params (string Name, JsonNode Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (n, v) in pairs) o[n] = v;
            return o;
        }

        // ------------------------------------------------------------------ the engine

        [AvaloniaTest]
        public void TheMachineNamesItsOutputsAndTheEngineKnowsThem()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var (multi, _) = ThreeChains(0);
            Assert.That(multi.OutputChannelCount, Is.EqualTo(3));
            Assert.That(Enumerable.Range(0, 3).Select(i => multi.GetChannelName(false, i)), Is.EqualTo(new[] { "Low", "Mid", "High" }));
            Assert.That(Add("Neo Utility", "Plain").OutputChannelCount, Is.EqualTo(1), "a machine with one output is as it was");
        }

        [AvaloniaTest]
        public void EachChainRendersFromItsOwnOutputAndTheMixIsTheirSumToTheLastBit()
        {
            using var restore = Studio.AfterTest(NewSong);
            const double seconds = 0.5;
            var solo = new float[3][];
            for (int k = 0; k < 3; k++)
            {
                NewSong();
                ThreeChains(1 << k);
                solo[k] = Render(seconds);
            }
            NewSong();
            var (multi, chain) = ThreeChains();
            var mix = Render(seconds);

            double[] want = { 110, 220, 440 };
            for (int k = 0; k < 3; k++)
            {
                double hz = HzOf(solo[k], seconds);
                TestContext.Out.WriteLine($"[notice] Chain {k + 1} from {multi.GetChannelName(false, k)}: {hz:F0} Hz (the output's pitch {want[k]:F0})");
                Assert.That(hz, Is.EqualTo(want[k]).Within(want[k] * 0.03), $"chain {k + 1} hears output {k + 1}'s pitch and no other");
                Assert.That(solo[k].Max(Math.Abs), Is.GreaterThan(1e-3), $"chain {k + 1} is audible");
            }
            var sum = new float[mix.Length];
            for (int i = 0; i < sum.Length; i++) sum[i] = ((0f + solo[0][i]) + solo[1][i]) + solo[2][i];
            int different = Enumerable.Range(0, sum.Length).Count(i => BitConverter.SingleToInt32Bits(sum[i]) != BitConverter.SingleToInt32Bits(mix[i]));
            TestContext.Out.WriteLine($"[notice] Routing: {mix.Length} samples, the mix against the sum of the three chains: {different} differ");
            Assert.That(different, Is.EqualTo(0), "the mix is the sum of the three chains, sample for sample");
            Assert.That(chain.Length, Is.EqualTo(3));
        }

        [AvaloniaTest]
        public void ANewWireFromOutputThreeAllocatesNothingOnTheAudioThread()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var (multi, chain) = ThreeChains(3);          // outputs 1 and 2 are wired
            using var audio = new AudioHygieneTests.AudioThread(1);
            lock (ReBuzzCore.AudioLock) Core.Playing = true;
            try
            {
                audio.Read(800);
                Wire(multi, chain[2], 2);                  // a new wire, from output 3, while it plays
                var first = audio.Read(1);
                TestContext.Out.WriteLine($"[notice] The first block after a new wire from output 3: {first.Bytes} bytes\n" + string.Join("\n", first.Log));
                Assert.That(first.Bytes, Is.EqualTo(0), "the first block after the new wire allocated:\n" + string.Join("\n", first.Log));
                var rest = audio.Read(2000);
                TestContext.Out.WriteLine($"[notice] 2000 blocks with all three outputs wired: {rest.Bytes} bytes");
                Assert.That(rest.Bytes, Is.EqualTo(0), string.Join("\n", rest.Log));
            }
            finally { lock (ReBuzzCore.AudioLock) Core.Playing = false; }
        }

        [AvaloniaTest]
        public void TheSourceOutputsAreSavedInTheSongAndAnOlderSongIsOnOutputOne()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var (multi, chain) = ThreeChains(3);
            Wire(multi, chain[2], 1);
            Core.SongCore.SetConnectionChannelMultiSelect(multi.Outputs.Single(o => o.Destination == chain[2]), false, 2, true);       // output 2 and 3 together
            var was = multi.Outputs.ToDictionary(o => o.Destination.Name, o => o.SourceChannels.OrderBy(x => x).ToArray());
            Assert.That(was["Chain 1"], Is.EqualTo(new[] { 0 }));
            Assert.That(was["Chain 2"], Is.EqualTo(new[] { 1 }));
            Assert.That(was["Chain 3"], Is.EqualTo(new[] { 1, 2 }));
            var before = Render(0.3);

            string path = Path.Combine(Path.GetTempPath(), "multi-out-" + Guid.NewGuid().ToString("N") + ".nbl");
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.TryOpenSongFile(path), Is.True, "reopened");
                var after = Song.MachinesList.Single(m => m.Name == "Multi");
                var now = after.Outputs.ToDictionary(o => o.Destination.Name, o => o.SourceChannels.OrderBy(x => x).ToArray());
                TestContext.Out.WriteLine("[notice] Save and load: " + string.Join("; ", now.Select(p => $"{p.Key} from output {string.Join("+", p.Value.Select(x => x + 1))}")));
                Assert.That(now, Is.EqualTo(was), "every wire reads the outputs it did");
                var again = Render(0.3);
                Assert.That(again, Is.EqualTo(before), "and the song renders bit for bit as it did");

                // An older song: one wire per machine from the main output (as every song before this had them) comes back on output 1 and plays the same.
                NewSong();
                var multi2 = Add("Multi Test", "Multi");
                Wire(multi2, Master, 0);
                var older = Render(0.3);
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.TryOpenSongFile(path), Is.True);
                var wire = Song.MachinesList.Single(m => m.Name == "Multi").Outputs.Single();
                Assert.That(wire.SourceChannels.ToArray(), Is.EqualTo(new[] { 0 }), "output 1");
                Assert.That(Render(0.3), Is.EqualTo(older), "bit for bit");
            }
            finally
            {
                Dialogs.Responder = null;
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void ConnectFromOutputTwoThenChangeItThenUndoTwiceLeavesTheGraphAsItWas()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var multi = Add("Multi Test", "Multi");
            var fx = Add("Neo Utility", "Fx");
            Wire(multi, Master, 0);
            Pump();
            string graph = MachineViewTests.Graph();
            var stack = (ManagedActionStack)Song.ActionStack;
            int steps = stack.Actions.Count();

            Assert.That(MachineEdits.Connect(Core, multi, fx, 1), Is.True);
            var wire = multi.Outputs.Single(o => o.Destination == fx);
            Assert.That(wire.SourceChannel, Is.EqualTo(1), "from output 2");
            MachineEdits.SetWireOutput(Core, wire, 2);
            Assert.That(wire.SourceChannels.ToArray(), Is.EqualTo(new[] { 2 }), "the picker moved it to output 3");
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 2), "a step each");

            Song.ActionStack.Undo();
            Assert.That(multi.Outputs.Single(o => o.Destination == fx).SourceChannels.ToArray(), Is.EqualTo(new[] { 1 }), "undo: back on output 2");
            Song.ActionStack.Undo();
            Assert.That(multi.Outputs.Any(o => o.Destination == fx), Is.False, "undo again: the wire is gone");
            Assert.That(MachineViewTests.Graph(), Is.EqualTo(graph), "the graph is as it was");
            Song.ActionStack.Redo();
            Song.ActionStack.Redo();
            Assert.That(multi.Outputs.Single(o => o.Destination == fx).SourceChannels.ToArray(), Is.EqualTo(new[] { 2 }), "redo twice: output 3 again");
        }

        // ------------------------------------------------------------------ the agent

        [AvaloniaTest]
        public void TheAgentConnectsFromAnOutputByNumberAndByNameAndSaysWhatItHas()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var multi = Add("Multi Test", "Multi");
            Add("Neo Utility", "Fx A");
            Add("Neo Utility", "Fx B");
            Add("Neo Utility", "Fx C");

            Call("connect", Args(("from", "Multi"), ("to", "Fx A"), ("from_output", "2")));
            Call("connect", Args(("from", "Multi"), ("to", "Fx B"), ("from_output", "high")));
            Call("connect", Args(("from", "Multi"), ("to", "Fx C")));
            Assert.That(multi.Outputs.Single(o => o.Destination.Name == "Fx A").SourceChannel, Is.EqualTo(1), "by number, from 1");
            Assert.That(multi.Outputs.Single(o => o.Destination.Name == "Fx B").SourceChannel, Is.EqualTo(2), "by name, any case");
            Assert.That(multi.Outputs.Single(o => o.Destination.Name == "Fx C").SourceChannel, Is.EqualTo(0), "none: the main output");

            var bad = Try("connect", Args(("from", "Multi"), ("to", "Master"), ("from_output", "Top")));
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("Low, Mid, High"), "the error lists the outputs");
            Assert.That(Try("connect", Args(("from", "Multi"), ("to", "Master"), ("from_output", "4"))).Text, Does.Contain("3 output"));

            var list = Call("machine_list");
            var row = list["machines"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "Multi")!;
            Assert.That(row["output_ports"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Low", "Mid", "High" }));
            Assert.That(row["outputs"]!.AsArray().Select(o => (o!["to"]!.GetValue<string>(), o["from_output"]!.GetValue<string>())),
                Is.EquivalentTo(new[] { ("Fx A", "Mid"), ("Fx B", "High"), ("Fx C", "Low") }), "each wire says which output it reads");
            var described = Call("machine_describe", Args(("machine", "Multi")));
            Assert.That(described["machine"]!["output_ports"]!.AsArray().Count, Is.EqualTo(3), "machine_describe lists them too");

            Call("connection_set", Args(("changes", new JsonArray(new JsonObject { ["from"] = "Multi", ["to"] = "Fx A", ["from_output"] = "Low" }))));
            Assert.That(multi.Outputs.Single(o => o.Destination.Name == "Fx A").SourceChannel, Is.EqualTo(0), "connection_set moves a wire to another output");
        }

        [AvaloniaTest]
        public void ASwapToOneWithOneOutputMovesTheWiresToOutputOneAndSaysSoAndUndoPutsThemBack()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var multi = Add("Multi Test", "Multi");
            Add("Neo Utility", "Fx A");
            Add("Neo Utility", "Fx B");
            Add("Neo Utility", "Fx C");
            Call("connect", Args(("from", "Multi"), ("to", "Fx A"), ("from_output", "1")));
            Call("connect", Args(("from", "Multi"), ("to", "Fx B"), ("from_output", "2")));
            Call("connect", Args(("from", "Multi"), ("to", "Fx C"), ("from_output", "Low")));
            Pump();

            var r = Try("machine_swap", Args(("machine", "Multi"), ("gear", "Neo Poly")));
            Assert.That(r.Error, Is.False, r.Text);
            Pump();
            var now = Song.MachinesList.Single(m => m.Name == "Multi");
            Assert.That(now.DLL.Name, Is.EqualTo("Neo Poly"));
            Assert.That(now.Outputs.Select(o => (o.Destination.Name, o.SourceChannel)), Is.EquivalentTo(new[] { ("Fx A", 0), ("Fx B", 0), ("Fx C", 0) }), "every wire is on output 1");
            var moved = r.Data["moved_wires"]!.AsArray();
            Assert.That(moved.Select(x => x!["to"]!.GetValue<string>()), Is.EqualTo(new[] { "Fx B" }), "only the wire that was on output 2 moved");
            Assert.That(r.Text, Does.Contain("Fx B").And.Contain("moved"), "and the reply says which");

            Call("undo");
            Pump();
            var back = Song.MachinesList.Single(m => m.Name == "Multi");
            Assert.That(back.DLL.Name, Is.EqualTo("Multi Test"));
            Assert.That(back.Outputs.Select(o => (o.Destination.Name, o.SourceChannel)), Is.EquivalentTo(new[] { ("Fx A", 0), ("Fx B", 1), ("Fx C", 0) }), "undo: the wires are on the outputs they were");

            // A swap to another three-output machine keeps each wire where it was.
            var r2 = Try("machine_swap", Args(("machine", "Multi"), ("gear", "Multi Test")));
            Assert.That(r2.Error, Is.False, r2.Text);
            Assert.That(r2.Data["moved_wires"], Is.Null);
        }
    }
}
