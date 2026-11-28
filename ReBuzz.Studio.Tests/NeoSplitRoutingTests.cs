// Neo Split through the engine's several outputs (docs/specs/neo-split.md, SPL1 #201; docs/specs/multi-output.md): the gates of its routing,
// against the real engine. A Multi Test (three sines, 110, 220 and 440 Hz) feeds a Neo Split in Bands mode with the crossovers at 155, 310
// and 6000 Hz, so that output 1 carries the 110 Hz, output 2 the 220 Hz, output 3 the 440 Hz and output 4 nothing (for the late wire, at 60, 90 and
// 200 Hz: outputs 3 and 4 carry the 110 Hz and the other two); each output goes to a chain of its own. Each chain renders from its output and the mix is their sum to the last bit; a wire added to output 4 during playback hears
// what a wire from the start hears (the split keeps running whether or not an output is wired), with 0 bytes on the audio thread; save and
// load keep each wire's output; the agent connects each output by "Out n" or by its number.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class NeoSplitRoutingTests
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

        static IMachineConnection Wire(IMachine from, IMachine to, int output)
        {
            Song.ConnectMachines(from, to, new[] { output }, new[] { 0 }, 0x4000, 0x4000);
            return from.Outputs.Single(o => o.Destination == to);
        }

        /// <summary>The source feeding the split, the split with the crossovers given (the default: 155, 310 and 6000 Hz) and four chains; the outputs in <paramref name="mask"/> are wired to their chain.</summary>
        static (MachineCore Split, MachineCore[] Chain) Setup(int mask = 15, string x1 = "155 Hz", string x2 = "310 Hz", string x3 = "6000 Hz")
        {
            var multi = Add("Multi Test", "Multi");
            var split = Add("Neo Split", "Split");
            Wire(multi, split, 0);
            var feed = multi.Outputs.Single(o => o.Destination == split);
            Song.SetConnectionChannelMultiSelect(feed, false, 1, true);
            Song.SetConnectionChannelMultiSelect(feed, false, 2, true);       // all three sines into the split
            Call("param_set", Args(("machine", "Split"), ("changes", new JsonArray(
                new JsonObject { ["param"] = "Crossover 1", ["value"] = x1 },
                new JsonObject { ["param"] = "Crossover 2", ["value"] = x2 },
                new JsonObject { ["param"] = "Crossover 3", ["value"] = x3 }))));
            var chain = new MachineCore[4];
            for (int k = 0; k < 4; k++)
            {
                chain[k] = Add("Neo Utility", "Chain " + (k + 1));
                if ((mask & (1 << k)) != 0) Wire(split, chain[k], k);
                Wire(chain[k], Master, 0);
            }
            Pump();
            return (split, chain);
        }

        static void Start()
        {
            lock (ReBuzzCore.AudioLock)
            {
                ReBuzzCore.masterInfo.PosInTick = 0;
                var sub = ReBuzzCore.subTickInfo;
                sub.PosInSubTick = 0;
                sub.CurrentSubTick = 0;
                sub.SubTickReminderCounter = 0;
            }
            Core.MasterVolume = 1;
        }

        /// <summary>The engine's output, left channel, in buffers of 256 frames; <paramref name="atBuffer"/> runs before each (its index).</summary>
        static float[] Render(double seconds, Action<int> atBuffer = null)
        {
            Start();
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            int frames = (int)Math.Round(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256) * 256;
            var y = new float[frames];
            for (int f = 0; f < frames; f += 256)
            {
                atBuffer?.Invoke(f / 256);
                work.MainAudioFillBuffer(buffer, 0, 512);
                for (int i = 0; i < 256; i++) y[f + i] = buffer[2 * i];
            }
            work.Stop();
            return y;
        }

        static double HzOf(float[] x, int from, double seconds)
        {
            int crossings = 0;
            for (int i = from + 1; i < x.Length; i++) if (x[i - 1] < 0 && x[i] >= 0) crossings++;
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

        [AvaloniaTest]
        public void EachOutputGoesToItsOwnChainAndTheMixIsTheirSumToTheLastBit()
        {
            using var restore = Studio.AfterTest(NewSong);
            const double seconds = 0.5;
            var solo = new float[4][];
            for (int k = 0; k < 4; k++)
            {
                NewSong();
                Setup(1 << k);
                solo[k] = Render(seconds);
            }
            NewSong();
            var (split, _) = Setup();
            var mix = Render(seconds);

            Assert.That(split.OutputChannelCount, Is.EqualTo(4));
            double[] want = { 110, 220, 440, 0 };
            for (int k = 0; k < 4; k++)
            {
                double peak = solo[k].Skip(4800).Max(Math.Abs);
                double hz = HzOf(solo[k], 4800, seconds - 0.1);
                TestContext.Out.WriteLine($"[notice] Chain {k + 1} from {split.GetChannelName(false, k)}: {hz:F0} Hz, peak {peak:F4} (the band's pitch {want[k]:F0})");
                if (want[k] == 0) Assert.That(peak, Is.LessThan(1e-3), "the top band holds none of the three tones");
                else
                {
                    Assert.That(hz, Is.EqualTo(want[k]).Within(want[k] * 0.03), $"chain {k + 1} hears the tone of its band and no other");
                    Assert.That(peak, Is.GreaterThan(0.01), $"chain {k + 1} is audible");
                }
            }
            var sum = new float[mix.Length];
            for (int i = 0; i < sum.Length; i++) sum[i] = (((0f + solo[0][i]) + solo[1][i]) + solo[2][i]) + solo[3][i];
            int different = Enumerable.Range(0, sum.Length).Count(i => BitConverter.SingleToInt32Bits(sum[i]) != BitConverter.SingleToInt32Bits(mix[i]));
            TestContext.Out.WriteLine($"[notice] Routing: {mix.Length} samples, the mix against the sum of the four chains: {different} differ");
            Assert.That(different, Is.EqualTo(0), "the mix is the sum of the four chains, sample for sample");
        }

        [AvaloniaTest]
        public void WiringOutputFourDuringPlaybackStartsWithoutAClickAndAllocatesNothing()
        {
            using var restore = Studio.AfterTest(NewSong);
            const double seconds = 0.6;
            const int wireAt = 40;      // buffers
            // Output 3 (the 110 Hz) is wired to its chain throughout, as it is when someone adds a chain to a split that is already working;
            // output 4 (the 220 and 440 Hz) is wired at buffer 40 against the same song with it wired from the start and without it.
            NewSong();
            Setup(12, "60 Hz", "90 Hz", "200 Hz");
            var always = Render(seconds);
            NewSong();
            Setup(4, "60 Hz", "90 Hz", "200 Hz");
            var without = Render(seconds);

            NewSong();
            var (split, chain) = Setup(4, "60 Hz", "90 Hz", "200 Hz");
            var late = Render(seconds, b => { if (b == wireAt) Wire(split, chain[3], 3); });
            int at = wireAt * 256;
            int early = Enumerable.Range(0, at).Count(i => BitConverter.SingleToInt32Bits(late[i]) != BitConverter.SingleToInt32Bits(without[i]));
            int different = Enumerable.Range(at, late.Length - at).Count(i => BitConverter.SingleToInt32Bits(late[i]) != BitConverter.SingleToInt32Bits(always[i]));
            TestContext.Out.WriteLine($"[notice] Output 4 wired at buffer {wireAt}: {at} samples before it, {early} differ from the song without it; {late.Length - at} after it, {different} differ from the same wire made at the start");
            Assert.That(early, Is.EqualTo(0), "nothing changes before the wire");
            Assert.That(different, Is.EqualTo(0), "the new wire hears what a wire from the start hears: the filters ran on, so the band starts in phase and at level");
            Assert.That(late.Skip(at).Zip(without.Skip(at), (x, y) => Math.Abs(x - y)).Max(), Is.GreaterThan(0.01), "and it is audible");

            // 0 bytes on the audio thread: a new wire from output 4 while it plays, then 2000 blocks.
            NewSong();
            var (split2, chain2) = Setup(7);
            using var audio = new AudioHygieneTests.AudioThread(1);
            lock (ReBuzzCore.AudioLock) Core.Playing = true;
            try
            {
                audio.Read(800);
                Wire(split2, chain2[3], 3);
                var first = audio.Read(1);
                TestContext.Out.WriteLine($"[notice] The first block after a new wire from output 4: {first.Bytes} bytes");
                Assert.That(first.Bytes, Is.EqualTo(0), string.Join("\n", first.Log));
                var rest = audio.Read(2000);
                TestContext.Out.WriteLine($"[notice] 2000 blocks with all four outputs wired: {rest.Bytes} bytes");
                Assert.That(rest.Bytes, Is.EqualTo(0), string.Join("\n", rest.Log));
            }
            finally { lock (ReBuzzCore.AudioLock) Core.Playing = false; }
        }

        [AvaloniaTest]
        public void SaveAndLoadKeepEachWiresOutput()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var (split, _) = Setup();
            var was = split.Outputs.ToDictionary(o => o.Destination.Name, o => o.SourceChannels.OrderBy(x => x).ToArray());
            for (int k = 0; k < 4; k++) Assert.That(was["Chain " + (k + 1)], Is.EqualTo(new[] { k }));
            var before = Render(0.3);

            string path = Path.Combine(Path.GetTempPath(), "neo-split-" + Guid.NewGuid().ToString("N") + ".nbl");
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.TryOpenSongFile(path), Is.True, "reopened");
                var after = Song.MachinesList.Single(m => m.Name == "Split");
                var now = after.Outputs.ToDictionary(o => o.Destination.Name, o => o.SourceChannels.OrderBy(x => x).ToArray());
                TestContext.Out.WriteLine("[notice] Save and load: " + string.Join("; ", now.OrderBy(p => p.Key).Select(p => $"{p.Key} from output {string.Join("+", p.Value.Select(x => x + 1))}")));
                Assert.That(now, Is.EqualTo(was), "every wire reads the output it did");
                Assert.That(Render(0.3), Is.EqualTo(before), "and the song renders bit for bit as it did");
            }
            finally
            {
                Dialogs.Responder = null;
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void TheAgentConnectsEachOutputByOutNOrByNumber()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var split = Add("Neo Split", "Split");
            for (int k = 1; k <= 4; k++) Add("Neo Utility", "Fx " + k);
            Call("connect", Args(("from", "Split"), ("to", "Fx 1"), ("from_output", "Out 4")));
            Call("connect", Args(("from", "Split"), ("to", "Fx 2"), ("from_output", "out 3")));
            Call("connect", Args(("from", "Split"), ("to", "Fx 3"), ("from_output", "2")));
            Call("connect", Args(("from", "Split"), ("to", "Fx 4")));
            int From(string fx) => split.Outputs.Single(o => o.Destination.Name == fx).SourceChannel;
            Assert.That((From("Fx 1"), From("Fx 2"), From("Fx 3"), From("Fx 4")), Is.EqualTo((3, 2, 1, 0)), "Out 4, out 3, 2 by number, none: the first");

            var bad = Try("connect", Args(("from", "Split"), ("to", "Master"), ("from_output", "Top")));
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("Out 1, Out 2, Out 3, Out 4"), "the error lists the outputs");
            var list = Call("machine_list");
            var row = list["machines"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "Split")!;
            Assert.That(row["output_ports"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Out 1", "Out 2", "Out 3", "Out 4" }));
            Assert.That(row["outputs"]!.AsArray().Select(o => (o!["to"]!.GetValue<string>(), o["from_output"]!.GetValue<string>())),
                Is.EquivalentTo(new[] { ("Fx 1", "Out 4"), ("Fx 2", "Out 3"), ("Fx 3", "Out 2"), ("Fx 4", "Out 1") }), "each wire says which output it reads");
        }
    }
}
