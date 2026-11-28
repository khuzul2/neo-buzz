// Fork (docs/specs/instrument-input.md, AI1a #229): audio into an instrument, end to end against the real engine with the tests' Input Test
// machine (a generator that declares one audio input: its input times a gate open while a note is held, plus a quiet sine) and Noise Test
// (a steady seeded noise). The engine's gates: routing sample for sample, the refusal for a generator without an input, the loop check,
// 0 bytes on the audio thread (the block after a new wire included), the wire saved in .nbl and undone and redone, and the agent's connect.

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
using ReBuzz.Agent.Mcp;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Tests
{
    public class InstrumentInputTests
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

        static IMachineConnection Wire(IMachine from, IMachine to, int amp = 0x4000)
        {
            Song.ConnectMachines(from, to, new[] { 0 }, new[] { 0 }, amp, 0x4000);
            return from.Outputs.Single(o => o.Destination == to);
        }

        static IParameter Param(MachineCore m, string name) => m.ParameterGroupsList[1].ParametersList.First(p => p.Name == name);

        /// <summary>A noise source with its own seed, wired to the destination at the amp (and not to Master).</summary>
        static MachineCore Noise(string name, int seed, IMachine to = null, int amp = 0x4000)
        {
            var n = Add("Noise Test", name);
            if (seed > 0) Param(n, "Seed").SetValue(0, seed);
            foreach (var w in n.Outputs.ToList()) Song.DisconnectMachines(w);
            if (to != null) Wire(n, to, amp);
            return n;
        }

        static MachineCore InputTest(string name = "Input")
        {
            var m = Add("Input Test", name);
            foreach (var w in m.Outputs.ToList()) Song.DisconnectMachines(w);
            Wire(m, Master);
            return m;
        }

        /// <summary>Blocks of 256 frames as the device's thread reads them: the left channel; <paramref name="onBlock"/> runs before each block.</summary>
        static float[] Render(int blocks, Action<int> onBlock = null)
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
            var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            var y = new float[blocks * 256];
            for (int b = 0; b < blocks; b++)
            {
                onBlock?.Invoke(b);
                work.MainAudioFillBuffer(buffer, 0, 512);
                for (int i = 0; i < 256; i++) y[b * 256 + i] = buffer[2 * i];
            }
            work.Stop();
            return y;
        }

        const int Note = 69;
        const int Blocks = 400;                 // 2.1 s at 48 kHz
        const int NoteOn = 40, NoteOff = 228;   // 188 blocks: 1 s

        static float[] RenderWithNote(MachineCore voice) =>
            Render(Blocks, b =>
            {
                if (b == NoteOn) voice.SendMIDINote(0, Note, 100);
                if (b == NoteOff) voice.SendMIDINote(0, Note, 0);
            });

        static bool Gate(int frame) => frame >= NoteOn * 256 && frame < NoteOff * 256;

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

        // ------------------------------------------------------------------ routing

        [AvaloniaTest]
        public void TheInputIsHeardWhileTheNoteIsHeldAndTheTwoSourcesSumWithTheirAmps()
        {
            using var restore = Studio.AfterTest(NewSong);

            // The sources as the master hears them, alone, each at the amp it will have into the instrument (the connection's amp is applied by
            // the wire, so a source at 0 dB and one at -6 dB: 0x4000 and 0x2000).
            NewSong();
            Noise("A", 3, Master, 0x4000);
            var a = Render(Blocks);
            NewSong();
            Noise("B", 5, Master, 0x2000);
            var b = Render(Blocks);
            NewSong();
            var voiceOnly = InputTest();
            var voice = RenderWithNote(voiceOnly);
            Assert.That(a.Max(Math.Abs), Is.GreaterThan(0.1f), "the noise is audible");
            Assert.That(voice.Skip(NoteOn * 256).Take(1000).Max(Math.Abs), Is.GreaterThan(1e-4f), "the voice plays while the note is held");
            Assert.That(voice.Take(NoteOn * 256).Max(Math.Abs), Is.EqualTo(0f), "and nothing before it");

            // One source into Input Test.
            NewSong();
            var one = InputTest();
            Noise("A", 3, one, 0x4000);
            var heard1 = RenderWithNote(one);
            int differ1 = 0, loud1 = 0;
            for (int i = 0; i < heard1.Length; i++)
            {
                float want = Gate(i) ? a[i] + voice[i] : 0f;
                if (Gate(i) && Math.Abs(a[i]) > 0.01f) loud1++;
                if (heard1[i] != want) differ1++;
            }
            TestContext.Out.WriteLine($"[notice] Routing: one source, {heard1.Length} samples, {differ1} differ from the reference (the input times the gate plus the voice); {loud1} gated samples carry the noise");
            Assert.That(differ1, Is.EqualTo(0), "the output is the input while the note is held and silence outside it, sample for sample");
            Assert.That(loud1, Is.GreaterThan(40000), "and the input was really there");

            // Two sources, A at 0 dB and B at -6 dB.
            NewSong();
            var two = InputTest();
            Noise("A", 3, two, 0x4000);
            Noise("B", 5, two, 0x2000);
            var heard2 = RenderWithNote(two);
            int differ2 = 0;
            for (int i = 0; i < heard2.Length; i++)
            {
                float want = Gate(i) ? (0f + a[i] + b[i]) + voice[i] : 0f;
                if (heard2[i] != want) differ2++;
            }
            TestContext.Out.WriteLine($"[notice] Routing: two sources summed with their connection amps, {heard2.Length} samples, {differ2} differ from the reference");
            Assert.That(differ2, Is.EqualTo(0), "two sources sum, each at its connection's amp");
        }

        [AvaloniaTest]
        public void AMutedInstrumentDropsItsInputAndBypassIsSilentToo()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var m = InputTest();
            Noise("A", 3, m);
            m.IsMuted = true;
            var muted = RenderWithNote(m);
            Assert.That(muted.Max(Math.Abs), Is.EqualTo(0f), "muted: no input and no voice reach the master");
            m.IsMuted = false;
            var live = RenderWithNote(m);
            Assert.That(live.Max(Math.Abs), Is.GreaterThan(0.1f), "unmuted: it plays");
            m.IsBypassed = true;
            var bypassed = RenderWithNote(m);
            Assert.That(bypassed.Max(Math.Abs), Is.EqualTo(0f), "bypassed, as any generator: silent, the input isn't passed on");
        }

        // ------------------------------------------------------------------ the rules

        [AvaloniaTest]
        public void AGeneratorWithoutAnInputRefusesTheWireAndOneThatDeclaresItTakesIt()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var poly = Add("Neo Poly", "Poly");
            var noise = Add("Noise Test", "Noise");
            var input = Add("Input Test", "Input");
            Assert.That(poly.InputChannelCount, Is.EqualTo(0), "Neo Poly declares no input");
            Assert.That(input.InputChannelCount, Is.EqualTo(1), "Input Test declares one");
            Assert.That(SongCore.TakesAudio(poly), Is.False);
            Assert.That(SongCore.TakesAudio(input), Is.True);
            Assert.That(Song.CanConnectMachines(noise, poly), Is.False, "the core refuses a wire into Neo Poly");
            Assert.That(Song.CanConnectMachines(noise, input), Is.True, "and takes one into Input Test");
            Assert.That(MachineEdits.Connect(Core, noise, poly), Is.False, "the view's connect refuses it");
            Assert.That(MachineEdits.WhyNotConnect(Core, noise, poly), Is.EqualTo("Poly is an instrument: it has no input"), "with the words it had");
            Assert.That(MachineEdits.WhyNotConnect(Core, noise, input), Is.Null);
            Assert.That(MachineEdits.Connect(Core, noise, input), Is.True);
            Assert.That(Song.CanConnectMachines(noise, input), Is.False, "already wired");
            Assert.That(poly.Inputs.Count, Is.EqualTo(0));
        }

        [AvaloniaTest]
        public void AWireThatWouldLoopBackIntoTheInstrumentIsRefused()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var input = Add("Input Test", "Input");
            var fx = Add("Neo Utility", "Fx");
            Wire(input, fx);
            Assert.That(Song.CanConnectMachines(fx, input), Is.False, "Input Test → Neo Utility → Input Test");
            Assert.That(MachineEdits.Connect(Core, fx, input), Is.False);
            Assert.That(MachineEdits.WhyNotConnect(Core, fx, input), Does.Contain("loop"));
            var r = Try("connect", Args(("from", "Fx"), ("to", "Input")));
            Assert.That(r.Error, Is.True);
            Assert.That(r.Text, Does.Contain("loop"), r.Text);
        }

        // ------------------------------------------------------------------ the audio thread

        [AvaloniaTest]
        public void TheInputAllocatesNothingOnTheAudioThreadTheBlockAfterANewWireIncluded()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var m = InputTest();
            var n1 = Noise("A", 0, m);                // seed 0: its default, no value written
            var late = Noise("B", 0, Master);        // already playing, to Master; it then also feeds the instrument
            m.SendMIDINote(0, Note, 100);
            Pump();
            using var audio = new AudioHygieneTests.AudioThread(1);
            lock (ReBuzzCore.AudioLock) Core.Playing = true;
            try
            {
                audio.Read(800);
                Thread.Sleep(400);                          // the runtime's tiered compilation settles (its first recompile of a hot method comes after a quiet 100 ms)
                var steady = audio.Read(2000, describe: true);
                TestContext.Out.WriteLine($"[notice] 2000 blocks with the input wired from the start, a note held: {steady.Bytes} bytes");
                Assert.That(steady.Bytes, Is.EqualTo(0), string.Join("\n", steady.Log));
                Wire(late, m, 0x2000);                      // a new wire into the instrument while it plays
                Pump();                                     // the UI thread's work after a wire (the delay compensation's rebuild) is done before the audio thread reads on
                var first = audio.Read(1, describe: true);
                TestContext.Out.WriteLine($"[notice] The first block after a new wire into the instrument: {first.Bytes} bytes\n" + string.Join("\n", first.Log));
                Assert.That(first.Bytes, Is.EqualTo(0), "the first block after the new wire allocated:\n" + string.Join("\n", first.Log));
                // As the hygiene tests read (AudioHygieneTests.Measured, GuardPolicy): a read that allocated is made again, up to the policy's attempts.
                // A new wire of any machine, an effect's too, leaves one 56-byte allocation outside every stage of the block, somewhere in the next
                // 1500 blocks, on some runs; steady state is 0 (8000 blocks with the input wired from the start measured 0 in each case).
                var rest = audio.Read(2000, describe: true);
                for (int attempt = 1; attempt < NeoBuzz.TestSupport.GuardPolicy.Attempts && rest.Bytes != 0; attempt++)
                    rest = audio.Read(2000, describe: true);
                TestContext.Out.WriteLine($"[notice] 2000 blocks with the input wired: {rest.Bytes} bytes");
                Assert.That(rest.Bytes, Is.EqualTo(0), string.Join("\n", rest.Log));
            }
            finally { lock (ReBuzzCore.AudioLock) Core.Playing = false; }
            Assert.That(n1.Outputs.Count, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ the song file and undo

        [AvaloniaTest]
        public void TheWireIsSavedInTheSongAndComesBackAndUndoAndRedoCoverIt()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var m = InputTest();
            Noise("A", 3, m, 0x3000);
            Pump();
            var before = RenderWithNote(m);
            string graph = MachineViewTests.Graph();

            string path = Path.Combine(Path.GetTempPath(), "instrument-input-" + Guid.NewGuid().ToString("N") + ".nbl");
            Dialogs.Responder = (mm, c, b) => UserAnswer.No;
            try
            {
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.TryOpenSongFile(path), Is.True, "reopened");
                var again = Song.MachinesList.Single(x => x.Name == "Input");
                var wire = again.Inputs.Single();
                Assert.That(wire.Source.Name, Is.EqualTo("A"));
                Assert.That(wire.Amp, Is.EqualTo(0x3000), "the wire's amp comes back");
                Assert.That(again.InputChannelCount, Is.EqualTo(1));
                Assert.That(MachineViewTests.Graph(), Is.EqualTo(graph), "the graph is as it was");
                var after = RenderWithNote(again);
                int differ = Enumerable.Range(0, after.Length).Count(i => after[i] != before[i]);
                TestContext.Out.WriteLine($"[notice] Save and load: the wire from {wire.Source.Name} at amp {wire.Amp:X}, {after.Length} samples rendered, {differ} differ from before the save");
                Assert.That(differ, Is.EqualTo(0), "and the song renders sample for sample as it did");
            }
            finally
            {
                Dialogs.Responder = null;
                File.Delete(path);
            }

            // Connect, undo, redo.
            NewSong();
            var input = Add("Input Test", "Input");
            var src = Add("Noise Test", "A");
            foreach (var w in src.Outputs.ToList()) Song.DisconnectMachines(w);
            Wire(input, Master);
            Pump();
            string empty = MachineViewTests.Graph();
            Assert.That(MachineEdits.Connect(Core, src, input), Is.True);
            string wired = MachineViewTests.Graph();
            Song.ActionStack.Undo();
            Assert.That(input.Inputs.Count, Is.EqualTo(0), "undo: the wire is gone");
            Assert.That(MachineViewTests.Graph(), Is.EqualTo(empty));
            Song.ActionStack.Redo();
            Assert.That(input.Inputs.Single().Source, Is.EqualTo(src), "redo: it is back");
            Assert.That(MachineViewTests.Graph(), Is.EqualTo(wired));

            // Insert a machine on the wire into the instrument, then undo it.
            var utility = Core.Instruments.First(i => i.MachineDLL.Name == "Neo Utility" && i.Name.Length == 0);
            MachineEdits.InsertIntoWire(Core, input.Inputs.Single(), utility);
            Pump();
            var fx = input.Inputs.Single().Source;
            Assert.That(fx.DLL.Name, Is.EqualTo("Neo Utility"), "the effect sits between the source and the instrument");
            Assert.That(fx.Inputs.Single().Source, Is.EqualTo(src));
            Song.ActionStack.Undo();
            Assert.That(input.Inputs.Single().Source, Is.EqualTo(src), "undo: the wire is as it was");
            Assert.That(MachineViewTests.Graph(), Is.EqualTo(wired));
        }

        [AvaloniaTest]
        public void ASwapToAGeneratorWithoutAnInputDropsTheWiresAndUndoBringsThemBack()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var m = InputTest();
            Noise("A", 3, m);
            Pump();
            var r = Try("machine_swap", Args(("machine", "Input"), ("gear", "Neo Poly")));
            Assert.That(r.Error, Is.False, r.Text);
            Pump();
            var now = Song.MachinesList.Single(x => x.Name == "Input");
            Assert.That(now.DLL.Name, Is.EqualTo("Neo Poly"));
            Assert.That(now.Inputs.Count, Is.EqualTo(0), "no wire into a generator that takes none");
            Call("undo");
            Pump();
            var back = Song.MachinesList.Single(x => x.Name == "Input");
            Assert.That(back.DLL.Name, Is.EqualTo("Input Test"));
            Assert.That(back.Inputs.Count, Is.EqualTo(1), "undo: the wire is back");
        }

        // ------------------------------------------------------------------ the agent

        [AvaloniaTest]
        public void TheAgentConnectsIntoTheInstrumentAndSaysWhyNotIntoAnother()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var input = Add("Input Test", "Input");
            var poly = Add("Neo Poly", "Poly");
            var noise = Add("Noise Test", "Noise");

            Call("connect", Args(("from", "Noise"), ("to", "Input"), ("gain_db", -6)));
            var wire = noise.Outputs.Single(o => o.Destination == input);
            Assert.That(wire.Amp, Is.EqualTo(0x2000).Within(0x40), "connected, at -6 dB");

            var bad = Try("connect", Args(("from", "Noise"), ("to", "Poly")));
            Assert.That(bad.Error, Is.True);
            Assert.That(bad.Text, Does.Contain("Poly is a generator and takes no audio input"), bad.Text);
            Assert.That(poly.Inputs.Count, Is.EqualTo(0));

            var list = Call("machine_list");
            var row = list["machines"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Input")!;
            Assert.That(row["audio_inputs"]!.GetValue<int>(), Is.EqualTo(1), "machine_list says it takes one audio input");
            Assert.That(row["inputs"]!.AsArray().Count, Is.EqualTo(1), "and its wire");
            var plain = list["machines"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Poly")!;
            Assert.That(plain["audio_inputs"], Is.Null, "a generator without one says nothing");
            var described = Call("machine_describe", Args(("machine", "Input")));
            Assert.That(described["machine"]!["audio_inputs"]!.GetValue<int>(), Is.EqualTo(1), "machine_describe too");

            Call("undo");
            Assert.That(input.Inputs.Count, Is.EqualTo(0), "undo takes the wire out");
        }
    }
}
