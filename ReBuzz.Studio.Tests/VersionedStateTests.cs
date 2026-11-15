// Fork (docs/specs/machines.md §3.4): versioned machine state on the real engine. The tests' Versioned Test effect (TestMachines/VersionedTest)
// went through three layouts; a song saved at each of them (a BMXML song rewritten to that layout, its data in the old form) opens in the
// current one with the same sound, a song saved now carries its state version in BMXML and BMX alike, and the agent's machine_describe and
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
using ReBuzzTests.Automation;
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

        /// <summary>The current song's BMXML with the machine rewritten to the layout and data of an older version.</summary>
        static void RewriteTo(string path, string machineName, int version)
        {
            var serializer = new XmlSerializer(typeof(BMXMLSong));
            BMXMLSong song;
            using (var f = File.OpenRead(path)) song = (BMXMLSong)serializer.Deserialize(f)!;
            var m = song.Machines.Single(x => x.Name == machineName);
            var globals = m.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global);
            var template = globals.Parameters[0];
            var layout = Layouts[version];
            globals.Parameters = layout.Select((name, i) => new BMXMLParameter
            {
                Type = template.Type, Name = name, Description = name, MinValue = 0, MaxValue = Max[version][i], NoValue = template.NoValue,
                Flags = template.Flags, DefValue = 0,
                Values = new[] { new BMXMLParameterValue { Track = 0, Value = SavedValue(version, name) } },
            }).ToArray();
            var content = Encoding.UTF8.GetBytes(DataAt(version));
            m.Data = StateEnvelope.Write(content, version);    // version 0: the plain envelope every old song has; later versions carry theirs
            using (var f = File.Create(path)) serializer.Serialize(f, song);
        }

        static void Open(string path)
        {
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.OpenSongFile(path);
            Pump();
        }

        [AvaloniaTest]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ASongSavedAtAnOlderVersionLoadsToTheSameSound(int version)
        {
            NewSong();
            string path = TestTemp.Path("versioned-" + Guid.NewGuid().ToString("N") + ".bmxml");
            try
            {
                Add("VT");
                Core.SaveSongCopy(path);
                RewriteTo(path, "VT", version);
                Open(path);
                var vt = Core.SongCore.MachinesList.Single(m => m.Name == "VT");
                Assert.That(Values(vt), Is.EqualTo(new[] { 800, 250, 2 }), $"cutoff, gain and mode of a v{version} song, in the current layout");
                Assert.That(Param(vt, "Cutoff").GetValue(0), Is.EqualTo(800));
                var (xml, v, first) = State(vt);
                Assert.That(v, Is.EqualTo(3), "saved again it is the current version");
                Assert.That(first, Is.EqualTo(StateEnvelope.Versioned));
                Assert.That(xml, Does.Contain("<Gain>7</Gain>").And.Not.Contain("Pan").And.Not.Contain("Level"), "its own data moved to the current form");
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        [AvaloniaTest]
        [TestCase(".bmxml")]
        [TestCase(".bmx")]
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

        [AvaloniaTest]
        public void ASongFromANewerBuildKeepsDefaultsAndLogsIt()
        {
            NewSong();
            string path = TestTemp.Path("versioned-" + Guid.NewGuid().ToString("N") + ".bmxml");
            try
            {
                Add("VT");
                Core.SaveSongCopy(path);
                var serializer = new XmlSerializer(typeof(BMXMLSong));
                BMXMLSong song;
                using (var f = File.OpenRead(path)) song = (BMXMLSong)serializer.Deserialize(f)!;
                var m = song.Machines.Single(x => x.Name == "VT");
                foreach (var p in m.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters) p.Values = new[] { new BMXMLParameterValue { Track = 0, Value = 77 } };
                m.Data = StateEnvelope.Write(Encoding.UTF8.GetBytes("<VersionedTestState><Future>1</Future></VersionedTestState>"), 9);
                using (var f = File.Create(path)) serializer.Serialize(f, song);
                Open(path);
                var vt = Core.SongCore.MachinesList.Single(x => x.Name == "VT");
                Assert.That(Values(vt), Is.EqualTo(new[] { 500, 500, 0 }), "the defaults, not another layout's numbers");
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        [AvaloniaTest]
        public void MachinesWithoutAVersionSaveWhatTheyAlwaysDid()
        {
            NewSong();
            try
            {
                foreach (var gear in new[] { "Neo Comp", "Neo Poly", "Neo Learn FM", "Neo Filter" })
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

        [AvaloniaTest]
        public void TheAgentSeesTheCurrentLayoutOfAnOldSong()
        {
            NewSong();
            string path = TestTemp.Path("versioned-" + Guid.NewGuid().ToString("N") + ".bmxml");
            try
            {
                Add("VT");
                Core.SaveSongCopy(path);
                RewriteTo(path, "VT", 0);
                Open(path);
                var d = Call("machine_describe", new JsonObject { ["machine"] = "VT" })["structuredContent"]!.AsObject();
                Assert.That(d["state_version"]!.GetValue<int>(), Is.EqualTo(3));
                var names = d["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray();
                Assert.That(names, Is.EqualTo(new[] { "Cutoff", "Gain", "Mode" }), "the current layout, not the song's");
                Call("param_set", new JsonObject
                {
                    ["machine"] = "VT",
                    ["changes"] = new JsonArray(new JsonObject { ["param"] = "Gain", ["value"] = "raw:300" }, new JsonObject { ["param"] = "#0", ["value"] = "raw:100" }),
                });
                var vt = Core.SongCore.MachinesList.Single(m => m.Name == "VT");
                Assert.That(Values(vt), Is.EqualTo(new[] { 100, 300, 2 }));
                var plain = Call("machine_describe", new JsonObject { ["machine"] = "VT" });
                Assert.That(plain.ToJsonString(), Does.Not.Contain("Old pan"));
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }
    }
}
