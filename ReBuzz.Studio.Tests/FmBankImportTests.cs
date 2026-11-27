// Fork (FU2b, neutral names): Neo FM6's bank import kind is "fm6_sysex_bank"; the first release's "dx7_bank" is a hidden alias, so existing agent scripts still work.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class FmBankImportTests
    {
        static JsonObject Tool(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(180);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        [AvaloniaTest]
        public void TheBankKindIsNeutralAndTheOldNameStillWorks()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            rig.Add("Neo FM6", "Keys");
            string file = Path.Combine(Path.GetTempPath(), "fm6-bank-import-" + Guid.NewGuid().ToString("N") + ".syx");
            File.WriteAllBytes(file, new byte[4096]);   // 32 voices of data with no header: a bank
            try
            {
                var described = Tool("machine_describe", new JsonObject { ["machine"] = "Keys" })["structuredContent"]!.AsObject();
                var kinds = described["data_imports"]!.AsArray().Select(i => (string)i!["kind"]!).ToList();
                Assert.That(kinds, Is.EqualTo(new[] { "fm6_sysex_bank" }), "the manifest lists the neutral kind only");
                Assert.That(described.ToJsonString().ToLowerInvariant(), Does.Not.Contain("dx7"), "no model number in what the agent reads");

                foreach (var kind in new[] { "fm6_sysex_bank", "dx7_bank" })
                {
                    var r = Tool("machine_data_import", new JsonObject { ["machine"] = "Keys", ["kind"] = kind, ["path"] = file, ["item"] = 1 });
                    Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, kind + ": " + r.ToJsonString());
                    Assert.That((string)r["structuredContent"]!["kind"]!, Is.EqualTo("fm6_sysex_bank"), kind + " is taken as the neutral kind");
                    Assert.That((string)r["structuredContent"]!["summary"]!, Does.Contain("32 voices"), kind);
                }

                var refused = Tool("machine_data_import", new JsonObject { ["machine"] = "Keys", ["kind"] = "wavetable", ["path"] = file });
                Assert.That(refused["isError"]?.GetValue<bool>(), Is.True, "another kind is still refused");
                Assert.That(refused.ToJsonString().ToLowerInvariant(), Does.Not.Contain("dx7"), "and the refusal does not name the old kind");
            }
            finally { File.Delete(file); }
        }
    }
}
