// Fork (docs/specs/note-wires.md): note wires in the real engine, with the tests' Test Note Echo machine (TestMachines/NoteEcho) and real Neo machines:
// a note arrives at the offset it left at, in the same block, for each block size and through chains of three; a source works before its target
// whichever way the audio graph alone would order them, in each work algorithm; a loop is refused with its reason; a track's insert chain takes
// the clip and live notes in order and closes up when an insert goes; the wires save, reopen, follow a rename, go with a deleted machine and
// come back with its undo; every edit is one undo step; a song saved with a Voice to MIDI target opens with the wire; 0 bytes a buffer with 16 wires.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.NoteWires;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NoteWireEngineTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static void NewSong()
        {
            lock (ReBuzzCore.AudioLock) Core.Playing = false;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Pump();
        }

        static MachineCore Add(string gear, string name)
        {
            var song = Core.SongCore;
            song.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var m = song.MachinesList.Last(x => x.Name == name);
            Pump();
            return m;
        }

        static void Audio(MachineCore from, MachineCore to) =>
            Core.SongCore.ConnectMachines(from, to, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);

        static MachineCore Master => Core.SongCore.MachinesList[0];

        /// <summary>A Note Echo, wired to the Master by audio so the engine works it.</summary>
        static MachineCore Echo(string name, int up = 0, int delay = 0, bool toMaster = true)
        {
            var m = Add("Test Note Echo", name);
            if (toMaster) Audio(m, Master);
            Param(m, "Up").SetValue(0, up);
            Param(m, "Delay").SetValue(0, delay);
            return m;
        }

        static IParameter Param(MachineCore m, string name) => m.ParameterGroupsList[1].ParametersList.First(p => p.Name == name);

        static T Echoed<T>(MachineCore m, Func<dynamic, T> read) => read(m.ManagedMachine);

        static object Field(MachineCore m, string name) => m.ManagedMachine.GetType().GetField(name).GetValue(m.ManagedMachine);
        static int[] Ints(MachineCore m, string name) => (int[])Field(m, name);
        static int Count(MachineCore m, string name) => (int)Field(m, name);
        static void Call(MachineCore m, string method, params object[] args) => m.ManagedMachine.GetType().GetMethod(method).Invoke(m.ManagedMachine, args);
        static (int Block, int Offset, int Kind, int Note)[] Received(MachineCore m)
        {
            int n = Count(m, "LogCount");
            var b = Ints(m, "LogBlock"); var o = Ints(m, "LogOffset"); var k = Ints(m, "LogKind"); var note = Ints(m, "LogNote");
            return Enumerable.Range(0, n).Select(i => (b[i], o[i], k[i], note[i])).ToArray();
        }
        static (int Block, int Offset, int Note)[] Sent(MachineCore m)
        {
            int n = Count(m, "SentCount");
            var b = Ints(m, "SentBlock"); var o = Ints(m, "SentOffset"); var note = Ints(m, "SentNote");
            return Enumerable.Range(0, n).Select(i => (b[i], o[i], note[i])).ToArray();
        }

        /// <summary>The engine as a device's thread reads it, with a work algorithm and a block size (frames), on this thread.</summary>
        sealed class Renderer : IDisposable
        {
            readonly WorkManager work;
            readonly WorkThreadEngine engine;
            readonly int algorithm;
            readonly bool bool2;
            readonly int frames;
            readonly float[] buffer;

            public Renderer(int frames, int algorithm = 0, bool multithreaded = false, bool cached = false)
            {
                this.frames = frames;
                this.algorithm = algorithm;
                buffer = new float[frames * 2];
                Core.EngineSettings.Set("Multithreading", multithreaded);
                Core.EngineSettings.Set("UseCachedWorkOrder", cached);
                // A buffer is cut at a tick (and a sub-tick) edge, and an event's offset is in the piece it is delivered in: so that the pieces
                // here are the buffers whatever the tests before left (the tick's position, a fast tempo), the tick is long and starts now.
                Core.EngineSettings.Set("SubTickTiming", false);
                Core.BPM = 20;
                ReBuzzCore.masterInfo.PosInTick = 0;
                engine = new WorkThreadEngine(4);
                if (algorithm == 2 && multithreaded) engine.Start();
                bool2 = multithreaded && algorithm == 2;
                work = new WorkManager(Core, engine, algorithm, Core.EngineSettings);
            }

            public void Blocks(int count, Action each = null)
            {
                for (int i = 0; i < count; i++)
                {
                    each?.Invoke();
                    work.MainAudioFillBuffer(buffer, 0, frames * 2);
                }
            }

            public void Dispose()
            {
                work.Stop();
                if (bool2) engine.Stop();
                Core.EngineSettings.Set("Multithreading", false);
                Core.EngineSettings.Set("UseCachedWorkOrder", false);
            }
        }

        // ------------------------------------------------------------------ offsets, blocks, chains

        [AvaloniaTest]
        public void ANoteArrivesAtTheOffsetItLeftAtInTheSameBlockForEveryBlockSizeThroughAChainOfThree([Values(16, 64, 256, 512)] int block)
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var src = Echo("Wsrc");
            var a = Echo("Wa");
            var b = Echo("Wb");
            var c = Echo("Wc");
            Assert.That(Core.NoteWires.Connect("Wsrc", "Wa"), Is.Null);
            Assert.That(Core.NoteWires.Connect("Wa", "Wb"), Is.Null);
            Assert.That(Core.NoteWires.Connect("Wb", "Wc"), Is.Null);
            Pump();
            using var r = new Renderer(block);
            r.Blocks(3);
            foreach (var m in new[] { src, a, b, c }) Call(m, "ClearLog");
            int[] offsets = { 0, 1, block / 2, block - 1 };
            int next = 0;
            r.Blocks(12, () => { Call(src, "EmitAt", offsets[next % 4], 40 + next % 40, true); next++; });
            r.Blocks(2);
            var sent = Sent(src);
            Assert.That(sent.Length, Is.EqualTo(12));
            foreach (var m in new[] { a, b, c })
            {
                var got = Received(m);
                Assert.That(got.Length, Is.EqualTo(12), m.Name);
                for (int i = 0; i < sent.Length; i++)
                {
                    Assert.That(got[i].Offset, Is.EqualTo(sent[i].Offset), $"{m.Name}: offset of event {i}");
                    Assert.That(got[i].Block, Is.EqualTo(sent[i].Block), $"{m.Name}: the block of event {i} (same block)");
                    Assert.That(got[i].Note, Is.EqualTo(sent[i].Note), $"{m.Name}: note of event {i}");
                }
            }
        }

        [AvaloniaTest]
        public void ATransposedEchoAndItsDelayedCopyCrossABlockEdge()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var src = Echo("Dsrc");
            var echo = Echo("Decho", up: 12, delay: 40);
            var end = Echo("Dend");
            Core.NoteWires.Connect("Dsrc", "Decho");
            Core.NoteWires.Connect("Decho", "Dend");
            Pump();
            using var r = new Renderer(64);
            r.Blocks(2);
            foreach (var m in new[] { src, echo, end }) Call(m, "ClearLog");
            Call(src, "EmitAt", 40, 50, true);
            r.Blocks(3);
            var got = Received(end);
            Assert.That(got.Select(g => (g.Offset, g.Note)), Is.EquivalentTo(new[] { (40, 62), (16, 62) }), "62 = 50 + 12 at the offset, and a copy 40 samples later, 16 into the next block");
            Assert.That(got.Select(g => g.Block).Distinct().Count(), Is.EqualTo(2));
        }

        public static object[] Algorithms => new object[]
        {
            new object[] { 0, false, false }, new object[] { 0, false, true },
            new object[] { 0, true, false }, new object[] { 0, true, true },
            new object[] { 1, true, false }, new object[] { 1, true, true },
            new object[] { 2, true, false }, new object[] { 2, true, true },
        };

        [AvaloniaTest]
        public void ASourceWorksBeforeItsTargetWhicheverWayTheAudioGraphAloneWouldOrderThem([ValueSource(nameof(Algorithms))] object[] mode)
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var one = Echo("Oone");
            var two = Echo("Otwo");
            foreach (var (from, to, first, second) in new[] { ("Oone", "Otwo", one, two), ("Otwo", "Oone", two, one) })
            {
                Assert.That(Core.NoteWires.Connect(from, to), Is.Null);
                Pump();
                using (var r = new Renderer(64, (int)mode[0], (bool)mode[1], (bool)mode[2])) r.Blocks(40);
                var a = (long[])Field(first, "SeqLog"); var b = (long[])Field(second, "SeqLog");
                int blocks = Math.Min(Count(first, "Block"), Count(second, "Block"));
                int late = 0;
                for (int i = Math.Max(1, blocks - 30); i < blocks; i++) if (a[i] >= b[i]) late++;
                Assert.That(late, Is.EqualTo(0), $"{from} -> {to} (algorithm {mode[0]}, threads {mode[1]}, cached {mode[2]}): the source worked first in every block");
                Core.NoteWires.Disconnect(from, to);
            }
        }

        [AvaloniaTest]
        public void ALoopIsRefusedWithItsReasonAndNothingChanges()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            Echo("Lone"); Echo("Ltwo"); Echo("Lthree");
            var wires = Core.NoteWires;
            Assert.That(wires.Connect("Lone", "Ltwo"), Is.Null);
            Assert.That(wires.Connect("Ltwo", "Lthree"), Is.Null);
            string why = wires.Connect("Lthree", "Lone");
            Assert.That(why, Does.Contain("loop"));
            Assert.That(why, Does.Contain("Lthree"));
            Assert.That(wires.State.Wires.Count, Is.EqualTo(2));
            Assert.That(wires.SetChain("Lone", new[] { "Lthree" }), Does.Contain("loop"), "a chain that would loop is refused too");
            Assert.That(wires.Connect("Lone", "Lone"), Is.Not.Null);
        }

        // ------------------------------------------------------------------ the track's insert chain

        [AvaloniaTest]
        public void ClipNotesAndLiveMidiPassTheInsertsInOrderAndRemovingOneClosesTheChain()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var inst = Echo("Cinst");
            var a = Echo("Ca", up: 12);
            var b = Echo("Cb", up: 7);
            Assert.That(Core.NoteWires.SetChain("Cinst", new[] { "Ca", "Cb" }), Is.Null);
            Pump();
            using var r = new Renderer(64);
            r.Blocks(2);
            foreach (var m in new[] { inst, a, b }) Call(m, "ClearLog");
            int NoteOn(int note) => 0x90 | (note << 8) | (100 << 16);
            int NoteOff(int note) => 0x80 | (note << 8);
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(inst, NoteOn(60), false));
            r.Blocks(1, () => Core.MachineManager.SendTrackNoteOff(inst, 0, 60));
            r.Blocks(2);
            Assert.That(Received(a).Select(g => g.Note), Is.EqualTo(new[] { 60, 60 }), "the first insert gets the track's notes (on and off)");
            Assert.That(Received(b).Select(g => g.Note), Is.EqualTo(new[] { 72, 72 }), "the second gets them after the first: +12");
            Assert.That(Received(inst).Select(g => g.Note), Is.EqualTo(new[] { 79, 79 }), "the instrument last: +7 more");
            Assert.That(Received(inst).Select(g => g.Kind), Is.EqualTo(new[] { (int)NoteMessageKind.NoteOn, (int)NoteMessageKind.NoteOff }));
            Assert.That(Received(inst)[0].Block, Is.EqualTo(Received(a)[0].Block), "all in the block the note came in");

            Assert.That(Core.NoteWires.RemoveInsert("Ca"), Is.True);
            Pump();
            foreach (var m in new[] { inst, a, b }) Call(m, "ClearLog");
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(inst, NoteOn(60), false));
            r.Blocks(2);
            Assert.That(Received(a), Is.Empty, "the removed insert hears nothing");
            Assert.That(Received(inst).Select(g => g.Note), Is.EqualTo(new[] { 67 }), "the chain closed up: the track's note goes through the second insert only");

            Assert.That(Core.NoteWires.RemoveInsert("Cb"), Is.True);
            Pump();
            foreach (var m in new[] { inst, a, b }) Call(m, "ClearLog");
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(inst, NoteOn(60), false));
            r.Blocks(2);
            Assert.That(Received(inst).Select(g => g.Note), Is.EqualTo(new[] { 60 }), "no chain: straight to the instrument as before");
        }

        // ------------------------------------------------------------------ Neo Chord, the first machine that takes and sends notes (CHD1)

        [AvaloniaTest]
        public void NeoChordAsAnInsertBeforeAnInstrumentAndOnAFreeWireTurnsNotesIntoChordsAndEndsThem()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var inst = Echo("Kinst");
            var chord = Add("Neo Chord", "Kch");
            Assert.That(Core.NoteWires.AddInsert("Kinst", "Kch"), Is.Null, "it takes and sends notes, so it can be an insert");
            Pump();
            using var r = new Renderer(64);
            r.Blocks(2);
            Call(inst, "ClearLog");
            int NoteOn(int note) => 0x90 | (note << 8) | (100 << 16);
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(inst, NoteOn(60), false));
            r.Blocks(1, () => Core.MachineManager.SendTrackNoteOff(inst, 0, 60));
            r.Blocks(2);
            var got = Received(inst);
            Assert.That(got.Where(g => g.Kind == (int)NoteMessageKind.NoteOn).Select(g => g.Note), Is.EqualTo(new[] { 60, 64, 67 }), "a major chord reaches the instrument");
            Assert.That(got.Where(g => g.Kind == (int)NoteMessageKind.NoteOff).Select(g => g.Note), Is.EquivalentTo(new[] { 60, 64, 67 }), "and all of it ends with the note");
            Assert.That(got.Where(g => g.Kind == (int)NoteMessageKind.NoteOn).Select(g => g.Block).Distinct().Count(), Is.EqualTo(1), "in the block the note came in");

            // Chord Off: the note passes as it was.
            Param(chord, "Chord").SetValue(0, 0);
            Call(inst, "ClearLog");
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(inst, NoteOn(62), false));
            r.Blocks(1, () => Core.MachineManager.SendTrackNoteOff(inst, 0, 62));
            r.Blocks(2);
            Assert.That(Received(inst).Select(g => g.Note), Is.EqualTo(new[] { 62, 62 }), "Off passes the note through");

            // On a free wire: the notes the machine gets are sent on to the instrument it is wired to.
            Assert.That(Core.NoteWires.RemoveInsert("Kch"), Is.True);
            var inst2 = Echo("Kinst2");
            Assert.That(Core.NoteWires.Connect("Kch", "Kinst2"), Is.Null);
            Param(chord, "Chord").SetValue(0, 2);
            Pump();
            Call(inst2, "ClearLog");
            r.Blocks(1, () => Core.MachineManager.SendMidiInput(chord, NoteOn(60), false));
            r.Blocks(2);
            Assert.That(Received(inst2).Where(g => g.Kind == (int)NoteMessageKind.NoteOn).Select(g => g.Note), Is.EqualTo(new[] { 60, 63, 67 }), "a minor chord down the free wire");
            Assert.That(Core.NoteWires.State.ChainOf("Kinst"), Is.Empty);
        }

        // ------------------------------------------------------------------ the song

        static string Temp(string name) => TestTemp.Path($"note-wires-{Environment.ProcessId}-{name}");

        [AvaloniaTest]
        public void WiresAndChainsSaveAndReopenAndFollowARenameAndGoWithADeletedMachineAndComeBackWithItsUndo()
        {
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; NewSong(); });
            NewSong();
            Echo("Sa"); Echo("Sb"); Echo("Sc"); Echo("Sd");
            var wires = Core.NoteWires;
            Assert.That(wires.Connect("Sa", "Sb"), Is.Null);
            Assert.That(wires.SetChain("Sd", new[] { "Sc" }), Is.Null);
            string path = Temp("a.nbl");
            Core.SaveSongCopy(path);
            NewSong();
            Assert.That(Core.NoteWires.State.IsEmpty, Is.True, "a new song has none");
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(Core.TryOpenSongFile(path), Is.True);
            Dialogs.Responder = null;
            wires = Core.NoteWires;
            Assert.That(wires.State.Wires, Is.EqualTo(new[] { new NoteEdge("Sa", "Sb") }), "the wire is back");
            Assert.That(wires.State.ChainOf("Sd"), Is.EqualTo(new[] { "Sc" }), "and the chain");
            Assert.That(Core.SongCore.MachinesList.First(m => m.Name == "Sa").NoteNode?.Out.Length, Is.EqualTo(1), "and live in the engine");

            // Rename.
            var sa = Core.SongCore.MachinesList.First(m => m.Name == "Sa");
            Core.RenameMachine(sa, "Sa2");
            Assert.That(wires.State.Wires, Is.EqualTo(new[] { new NoteEdge("Sa2", "Sb") }), "the rename carries the wire");
            var sc = Core.SongCore.MachinesList.First(m => m.Name == "Sc");
            Core.RenameMachine(sc, "Sc2");
            Core.RenameMachine(Core.SongCore.MachinesList.First(m => m.Name == "Sd"), "Sd2");
            Assert.That(wires.State.ChainOf("Sd2"), Is.EqualTo(new[] { "Sc2" }), "and the chain, both ends");

            // Delete either end: the wire goes quiet, is not saved, and the undo brings it back.
            var sb = Core.SongCore.MachinesList.First(m => m.Name == "Sb");
            Core.SongCore.DeleteMachines(new IMachine[] { sb });
            Pump();
            Assert.That(Core.SongCore.MachinesList.First(m => m.Name == "Sa2").NoteNode, Is.Null, "no live wire to a deleted machine");
            string b = Temp("b.nbl");
            Core.SaveSongCopy(b);
            Core.SongCore.ActionStack.Undo();
            Pump();
            Assert.That(Core.SongCore.MachinesList.First(m => m.Name == "Sa2").NoteNode?.Out.Length, Is.EqualTo(1), "the undo of the delete brings the wire back");

            // The song saved while Sb was gone has no wire to it (the file's sections are compressed: open it and look at the state).
            Core.Modified = false;
            Dialogs.Responder = (m, c, bb) => UserAnswer.No;
            Assert.That(Core.TryOpenSongFile(b), Is.True);
            Dialogs.Responder = null;
            Assert.That(Core.NoteWires.State.Wires, Is.Empty, "the wire to a deleted machine isn't saved");
            Assert.That(Core.NoteWires.State.ChainOf("Sd2"), Is.EqualTo(new[] { "Sc2" }), "the chain of the machines that are there is");
        }

        [AvaloniaTest]
        public void EveryEditIsOneUndoStepAndRedoRepeatsIt()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            Echo("Ua"); Echo("Ub"); Echo("Uc");
            var wires = Core.NoteWires;
            var stack = Core.SongCore.ActionStack;
            Assert.That(wires.Connect("Ua", "Ub"), Is.Null);
            stack.Undo();
            Assert.That(wires.State.IsEmpty, Is.True, "one undo takes the wire away");
            stack.Redo();
            Assert.That(wires.State.Wires.Count, Is.EqualTo(1), "redo puts it back");
            Assert.That(wires.SetChain("Uc", new[] { "Ua", "Ub" }), Is.Null);
            stack.Undo();
            Assert.That(wires.State.Chains, Is.Empty, "one undo takes the whole chain away");
            stack.Redo();
            Assert.That(wires.State.ChainOf("Uc"), Is.EqualTo(new[] { "Ua", "Ub" }));
            Assert.That(wires.RemoveInsert("Ua"), Is.True);
            stack.Undo();
            Assert.That(wires.State.ChainOf("Uc"), Is.EqualTo(new[] { "Ua", "Ub" }), "removing an insert is one step too");
            Assert.That(wires.Disconnect("Ua", "Ub"), Is.True);
            stack.Undo();
            Assert.That(wires.State.Wires.Count, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ the old single target

        [AvaloniaTest]
        public void ASongSavedWithAVoiceToMidiTargetOpensWithTheWire()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var voice = Add("Neo Voice to MIDI", "Mvoice");
            var target = Echo("Mtarget");
            // What a song saved before note wires carries: the machine's state names the target.
            var machine = voice.ManagedMachine;
            var prop = machine.GetType().GetProperty("MachineState");
            var state = prop.GetValue(machine);
            state.GetType().GetProperty("Target").SetValue(state, "Mtarget");
            prop.SetValue(machine, state);
            Assert.That(Core.NoteWires.State.IsEmpty, Is.True, "nothing yet: the host takes it when the song has opened");
            typeof(NoteWireStore).GetMethod("AdoptLegacy", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Core.NoteWires, null);
            Core.NoteWires.Rebuild();
            Assert.That(Core.NoteWires.State.Wires, Is.EqualTo(new[] { new NoteEdge("Mvoice", "Mtarget") }));
            Assert.That(voice.ManagedMachine.GetType().GetProperty("TargetName").GetValue(voice.ManagedMachine), Is.EqualTo("Mtarget"), "the machine shows it as its target");
            Assert.That(prop.GetValue(machine).GetType().GetProperty("Target").GetValue(prop.GetValue(machine)), Is.EqualTo(""), "and no longer saves it itself");
            // Its menu makes and removes wires: the same one step as a drag.
            voice.ManagedMachine.GetType().GetMethod("SetTarget").Invoke(voice.ManagedMachine, new object[] { "" });
            Assert.That(Core.NoteWires.State.Wires, Is.Empty);
        }

        [AvaloniaTest]
        public void TheCatalogueAndTheNeoMachinesAgreeOnNotePorts()
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "Manifests", "machine-catalog.json"))) dir = Path.GetDirectoryName(dir);
            Assert.That(dir, Is.Not.Null, "the catalogue is in the repository");
            var machines = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "Machines", "Manifests", "machine-catalog.json")))["machines"].AsObject();
            int checkedCount = 0;
            foreach (var kv in machines)
            {
                bool wantIn = (bool?)kv.Value["note_in"] == true, wantOut = (bool?)kv.Value["note_out"] == true;
                if (!(kv.Key.StartsWith("Neo ") || kv.Key == "Dye Essence")) continue;
                try { Core.SongCore.CreateMachine(kv.Key, null, "Cat " + checkedCount, null, null, null, -1, 0.3f, 0.3f); }
                catch (Exception) { continue; }
                var m = Core.SongCore.MachinesList.LastOrDefault(x => x.Name == "Cat " + checkedCount);
                if (m == null) continue;
                Pump();
                checkedCount++;
                var ports = NoteWireStore.PortsOf(m);
                Assert.That(ports, Is.Not.Null, kv.Key);
                Assert.That(ports.Value.In, Is.EqualTo(wantIn), kv.Key + " takes notes");
                Assert.That(ports.Value.Out, Is.EqualTo(wantOut), kv.Key + " sends notes");
            }
            Assert.That(checkedCount, Is.GreaterThan(30), "most of the Neo family was created and checked");
        }

        // ------------------------------------------------------------------ the audio thread

        [AvaloniaTest]
        public void SixteenActiveWiresAllocateNothingPerBuffer([Values(0, 1, 2)] int algorithm)
        {
            using var restore = Studio.AfterTest(NewSong);
            NewSong();
            var src = Echo("Asrc");
            var sinks = Enumerable.Range(0, 16).Select(i => Echo("Asink" + i, up: i % 3)).ToArray();
            foreach (var s in sinks) Assert.That(Core.NoteWires.Connect("Asrc", s.Name), Is.Null);
            Pump();
            using var audio = new AudioHygieneTests.AudioThread(algorithm);
            int n = 0;
            audio.Read(40, () => { Call(src, "EmitAt", n++ % 200, 60, n % 2 == 0); });            // warm up
            n = 0;
            var cost = audio.Read(120, () => { Call(src, "EmitAt", n++ % 200, 60, n % 2 == 0); }, describe: false);
            TestContext.Out.WriteLine($"[notice] 16 note wires, algorithm {algorithm}: {cost.Bytes} bytes over 120 buffers");
            Assert.That(sinks.Sum(s => Count(s, "LogCount")), Is.GreaterThan(1000), "the wires carried notes");
            // The test machine's own bookkeeping (EmitAt, its lock) allocates nothing either; its log is preallocated.
            Assert.That(cost.Bytes, Is.EqualTo(0), string.Join("; ", cost.Log));
        }
    }
}
