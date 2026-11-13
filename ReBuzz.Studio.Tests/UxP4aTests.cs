// Fork (docs/specs/ux-overhaul.md P4a): the export renders off the UI thread; renders of the Neo instruments repeat within one process;
// one button, menu and dialog style; transitions of 150 ms or less; Tab reaches every control; every icon-only control has a name.

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
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP4aTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            Wait(task);
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static void Wait(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(180);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail("didn't finish in time");
                Thread.Sleep(2);
            }
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

        /// <summary>A fresh song with one instrument playing a few notes over a few bars.</summary>
        static MainWindow SongWith(string gear, int beats = 16)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            Call("machine_add", Args(("gear", gear), ("name", "Voice")));
            Call("pattern_create", Args(("machine", "Voice"), ("name", "P"), ("length_beats", beats)));
            var notes = new JsonArray();
            for (int b = 0; b < beats; b += 2)
            {
                notes.Add(new JsonObject { ["pitch"] = 48 + (b / 2) % 5 * 2, ["start"] = (double)b, ["length"] = 1.5, ["velocity"] = 100 });
                notes.Add(new JsonObject { ["pitch"] = 55 + (b / 2) % 3 * 3, ["start"] = (double)b + 0.5, ["length"] = 1.0, ["velocity"] = 80 });
            }
            Call("notes_write", Args(("machine", "Voice"), ("pattern", "P"), ("notes", notes)));
            Call("arrangement_place", Args(("machine", "Voice"), ("pattern", "P"), ("at", 0)));
            Settle();
            return window;
        }

        static void Restore(MainWindow window, string folder)
        {
            ExportSheet.FolderOpener = null;
            ExportSheet.Chooser = null;
            ExportSheet.OnProgress = null;
            Core.Looping = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            window.Close();
            Dialogs.Responder = null;
            Dispatcher.UIThread.RunJobs();
            if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // ------------------------------------------------------------------ renders repeat

        [AvaloniaTest]
        [TestCase("Neo Poly")]
        [TestCase("Neo FM6")]
        [TestCase("Neo Wave")]
        [TestCase("Neo Drums")]
        public void TwoRendersOfTheSameSongInOneProcessAreTheSameBytes(string gear)
        {
            var window = SongWith(gear);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            string first = Call("render", Args(("name", "a")))["file"]!.GetValue<string>();
            string second = Call("render", Args(("name", "b")))["file"]!.GetValue<string>();
            string third = Call("render", Args(("name", "c"), ("bits", 16)))["file"]!.GetValue<string>();
            var a = File.ReadAllBytes(first);
            var b = File.ReadAllBytes(second);
            Assert.That(a.Length, Is.GreaterThan(10000));
            Assert.That(ReBuzz.Agent.Audio.Wav.Read(first).Left.Max(Math.Abs), Is.GreaterThan(0.001f), gear + " made sound");
            Assert.That(b, Is.EqualTo(a), gear + ": the second render is the first's bytes");
            var c = File.ReadAllBytes(Call("render", Args(("name", "d")))["file"]!.GetValue<string>());
            Assert.That(c, Is.EqualTo(a), gear + ": and the fourth");
        }

        // ------------------------------------------------------------------ the export renders off the UI thread

        [AvaloniaTest]
        public void TheExportRendersOnAWorkerThreadWhileTheUiKeepsRunningAndEditsAreBlocked()
        {
            var window = SongWith("Neo Poly", 32);
            string folder = Path.Combine(Path.GetTempPath(), "ux-p4a-export-" + Environment.ProcessId);
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            ExportSheet.FolderOpener = _ => { };
            int ui = Environment.CurrentManagedThreadId;
            var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
            var voice = Core.SongCore.Machines.First(m => m.Name == "Voice");
            Action<float[], bool, BuzzGUI.Interfaces.SongTime> tap = (s, st, t) => threads.Add(Environment.CurrentManagedThreadId);
            voice.Outputs[0].Tap += tap;
            var sheet = new ExportSheet(Core);
            sheet.Show(window);
            Settle();
            sheet.PathBox.Text = Path.Combine(folder, "bg.wav");
            int reports = 0, pumps = 0;
            bool blockedDuring = false, flagDuring = false;
            ExportSheet.OnProgress = _ =>
            {
                reports++;
                blockedDuring = !window.IsEnabled;
                flagDuring = ExportSheet.Rendering;
            };
            var run = sheet.Run();
            while (!run.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                pumps++;
                Thread.Sleep(2);
                Assert.That(pumps, Is.LessThan(90000), "the export finished");
            }
            voice.Outputs[0].Tap -= tap;
            Assert.That(run.Result, Is.True, sheet.Status.Text);
            Assert.That(threads, Is.Not.Empty, "the tap heard the render");
            Assert.That(threads, Does.Not.Contain(ui), "the render ran on no UI thread");
            Assert.That(pumps, Is.GreaterThan(10), "the UI thread kept pumping while it rendered");
            Assert.That(reports, Is.GreaterThanOrEqualTo(1), "progress came to the sheet, on the UI thread");
            Assert.That(blockedDuring, Is.True, "the main window took no input while it rendered");
            Assert.That(flagDuring, Is.True, "the autosave and snapshot timers know");
            Assert.That(window.IsEnabled, Is.True, "and takes input again");
            Assert.That(ExportSheet.Rendering, Is.False);
            Assert.That(Core.Playing, Is.False);
            // The bytes are those of the tool's own export (the same render, off the UI thread or on it).
            var args = sheet.Arguments();
            args["path"] = Path.Combine(folder, "tool.wav");
            Call("export_audio", args);
            Assert.That(File.ReadAllBytes(Path.Combine(folder, "bg.wav")), Is.EqualTo(File.ReadAllBytes(Path.Combine(folder, "tool.wav"))));
        }
    }
}
