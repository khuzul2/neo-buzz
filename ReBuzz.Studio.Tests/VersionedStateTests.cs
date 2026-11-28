// Fork (docs/specs/machines.md §3.4): versioned machine state on the real engine. The tests' Versioned Test effect (TestMachines/VersionedTest)
// went through three layouts; a .nbl saved at an older one loads through MigrateKeyed, a song saved now carries its state version, and the agent's machine_describe and
// param_set speak the current layout. Every existing Neo machine saves what it always did (state version 0: byte 2 envelope).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;
using static ReBuzz.Studio.Tests.AudioHygieneTests;

namespace ReBuzz.Studio.Tests
{
    public class VersionedStateTests
    {
        const string Gear = "Versioned Test";

        static ReBuzzCore Core => AudioHygieneTests.Core;

        static MachineCore Add(string name)
        {
            Core.SongCore.CreateMachine(Gear, null, name, null, null, null, -1, 0.5f, 0.5f);
            return Core.SongCore.MachinesList.Last(m => m.Name == name);
        }

        static IParameter Param(IMachine m, string name) => m.ParameterGroups[1].Parameters.First(p => p.Name == name);

        static int[] Values(IMachine m) => m.ParameterGroups[1].Parameters.Select(p => p.GetValue(0)).ToArray();

        static (string Xml, int Version, byte First) State(MachineCore m)
        {
            byte[] data = m.Data;
            var (content, version) = StateEnvelope.Read(data);
            return (Encoding.UTF8.GetString(content), version, data[0]);
        }

        // v0 level, freq, mode, old_pan | v1 freq, level, mode, old_pan | v2 cutoff, gain, mode, old_pan | v3 cutoff, gain, mode
        static readonly string[][] Layouts =
        {
            new[] { "Level", "Freq", "Mode", "Old pan" },
            new[] { "Freq", "Level", "Mode", "Old pan" },
            new[] { "Cutoff", "Gain", "Mode", "Old pan" },
        };
        static readonly int[][] Max = { new[] { 100, 1000, 2, 10 }, new[] { 1000, 100, 2, 10 }, new[] { 1000, 1000, 2, 10 } };

        /// <summary>The settings: cutoff 800/1000, gain 0.25, mode 2, a pan of 9 (until v3), as a song saved at <paramref name="version"/> holds them.</summary>
        static int SavedValue(int version, string name) => name switch
        {
            "Freq" or "Cutoff" => 800,
            "Level" => 25,                            // v0 and v1 saved the level 0…100
            "Gain" => 250,
            "Mode" => 2,
            "Old pan" => 9,
            _ => throw new ArgumentException(name),
        };

        static string DataAt(int version) => version switch
        {
            0 or 1 => "<VersionedTestState><Level>7</Level><Pan>5</Pan></VersionedTestState>",
            _ => "<VersionedTestState><Gain>7</Gain><Pan>5</Pan></VersionedTestState>",
        };

        static void Open(string path)
        {
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.OpenSongFile(path);
            Pump();
        }

        [AvaloniaTest]
        [TestCase(".nbl")]
        public void ASongSavedNowKeepsItsStateVersion(string extension)
        {
            NewSong();
            string path = TestTemp.Path("versioned-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                var vt = Add("VT");
                Param(vt, "Cutoff").SetValue(0, 321);
                Param(vt, "Gain").SetValue(0, 654);
                Param(vt, "Mode").SetValue(0, 1);
                Core.SaveSongCopy(path);
                Open(path);
                vt = Core.SongCore.MachinesList.Single(m => m.Name == "VT");
                Assert.That(Values(vt), Is.EqualTo(new[] { 321, 654, 1 }));
                var (_, version, first) = State(vt);
                Assert.That(version, Is.EqualTo(3));
                Assert.That(first, Is.EqualTo(StateEnvelope.Versioned));
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        /// <summary>native-format.md §5: a .nbl holds each global as (key, raw) with the machine's StateVersion and loads it through MigrateKeyed.</summary>
        [AvaloniaTest]
        public void ANblSavesParametersByKeyAndLoadsAnOlderVersionThroughMigrateKeyed()
        {
            NewSong();
            string path = TestTemp.Path("versioned-keyed-" + Guid.NewGuid().ToString("N") + ".nbl"), old = path + ".v1.nbl";
            try
            {
                var vt = Add("VT");
                Param(vt, "Cutoff").SetValue(0, 321);
                Param(vt, "Gain").SetValue(0, 654);
                Core.SaveSongCopy(path);
                System.Text.Json.Nodes.JsonObject manifest;
                var set = new ReBuzz.FileOps.Nbl.NblBlobSet();
                using (var r = new ReBuzz.FileOps.Nbl.NblContainer.Reader(path))
                {
                    manifest = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(r.Manifest);
                    foreach (var h in r.Hashes) { var (codec, stored) = r.GetStored(h); set.AddStored(h, codec, stored); }
                }
                var entry = manifest["machines"].AsArray().Single(m => (string)m["name"] == "VT");
                Assert.That((int)entry["stateVersion"], Is.EqualTo(3));
                Assert.That((int)entry["params"]["cutoff"], Is.EqualTo(321), "saved by key");
                Assert.That((int)entry["params"]["gain"], Is.EqualTo(654));

                // As a build with version 1's layout saved it: freq 800, level 25 (of 100), mode 2, old pan 9.
                entry["stateVersion"] = 1;
                entry["params"] = new System.Text.Json.Nodes.JsonObject { ["freq"] = 800, ["level"] = 25, ["mode"] = 2, ["old_pan"] = 9 };
                ReBuzz.FileOps.Nbl.NblContainer.WriteAtomic(old, System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()), set);
                Open(old);
                vt = Core.SongCore.MachinesList.Single(m => m.Name == "VT");
                Assert.That(Values(vt), Is.EqualTo(new[] { 800, 250, 2 }), "through the machine's own migration: renamed, rescaled, the pan dropped");
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); File.Delete(old); } catch (IOException) { }
                NewSong();
            }
        }

        [AvaloniaTest]
        public void MachinesWithoutAVersionSaveWhatTheyAlwaysDid()
        {
            NewSong();
            try
            {
                // Neo Poly left this list at AC1: it has a version now (1, F1 type grew a choice and the acid parameters were appended), see NeoPolyAcidSaveIsVersioned. Neo Filter has one too (1, the Vowel type and its three parameters were appended, EXT1 #190), see NeoFilterUnchangedTests and FilterMigrations.
                foreach (var gear in new[] { "Neo Comp", "Neo Learn FM" })
                {
                    Core.SongCore.CreateMachine(gear, null, "V0 " + gear, null, null, null, -1, 0.5f, 0.5f);
                    var m = Core.SongCore.MachinesList.Last(x => x.Name == "V0 " + gear);
                    var (_, version, first) = State(m);
                    Assert.That(version, Is.EqualTo(0), gear);
                    Assert.That(first, Is.EqualTo(StateEnvelope.Plain), gear + " saves the plain envelope: byte for byte as before versions");
                    Assert.That(m.ManagedMachine, Is.InstanceOf<IVersionedMachine>(), gear);
                    var vm = (IVersionedMachine)m.ManagedMachine;
                    Assert.That(vm.StateVersion, Is.EqualTo(0));
                    Assert.That(vm.ParameterKeys.Count, Is.EqualTo(m.ParameterGroups[1].Parameters.Count(p => p.Flags.HasFlag(ParameterFlags.State))), gear + ": one key per saved parameter");
                }
            }
            finally
            {
                NewSong();
            }
        }

        [AvaloniaTest]
        public void NeoPolyAcidSaveIsVersioned()
        {
            NewSong();
            try
            {
                Core.SongCore.CreateMachine("Neo Poly", null, "V1 Poly", null, null, null, -1, 0.5f, 0.5f);
                var m = Core.SongCore.MachinesList.Last(x => x.Name == "V1 Poly");
                var (_, version, _) = State(m);
                Assert.That(version, Is.EqualTo(3), "Neo Poly is at state version 3 (docs/specs/machines.md: acid, then the LFO Feels of SYN1)");
                Assert.That(((IVersionedMachine)m.ManagedMachine).StateVersion, Is.EqualTo(3));
            }
            finally
            {
                NewSong();
            }
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Call(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            Assert.That(r["error"], Is.Null, r.ToJsonString());
            var result = r["result"]!.AsObject();
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result;
        }

    }
}
