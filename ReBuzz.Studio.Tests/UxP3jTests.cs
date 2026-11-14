// Fork (docs/specs/ux-overhaul.md P3j, decisions 91-96): the empty Machine View's starters never over a machine box; a lit box keeps its name
// readable; Export Audio (⇧⌘R): the sheet's settings, one code path with export_audio (the same bytes), progress and cancel, overwrite,
// the folder opened when it is done.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class UxP3jTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ the starters' place

        [AvaloniaTest]
        public void TheStartersOfAnEmptyMachineViewNeverCoverAMachineBoxAtAnyWindowSize()
        {
            var (window, view) = OpenEmpty();
            try
            {
                // As the gallery reaches it: another view first, then the Machine View (it was hidden, with no size, when the song was made).
                window.ShowView("Sequence");
                Settle();
                window.ShowView("Machines");
                Settle();
                window.Detail.ShowPage(true, false);          // and the detail panel under it (mac-018)
                Settle();
                Settle();
                AssertClear(view, "after another view, with the detail panel");
                window.Detail.Close();
                Settle();
                var sizes = new[] { (1280, 800), (1000, 700), (900, 560), (760, 480), (640, 420), (520, 360), (420, 300), (1920, 1080) };
                foreach (var (w, h) in sizes)
                {
                    window.Width = w;
                    window.Height = h;
                    Settle();
                    Settle();
                    AssertClear(view, $"{w}x{h}");
                    if (w >= 760 && h >= 480) Assert.That(view.Starters.Opacity, Is.EqualTo(1), $"{w}x{h}: shown whole at an ordinary size (canvas {view.Canvas.Bounds}, block {view.Starters.DesiredSize}, margin {view.Starters.Margin}, opacity {view.Starters.Opacity})");
                }
            }
            finally
            {
                Close(window);
            }
        }

        static void AssertClear(MachineView view, string where)
        {
            var canvas = view.Canvas;
            Assert.That(view.Starters.IsVisible, Is.True, $"{where}: an empty song offers the starters");
            if (view.Starters.Opacity == 0) return;           // no room: hidden, and then nothing is over the Master
            var starters = new Rect(view.Starters.Bounds.Position, view.Starters.Bounds.Size);
            Assert.That(starters.Width, Is.GreaterThan(100), where);
            foreach (var m in canvas.Machines)
            {
                var box = canvas.BoxOf(m);
                var tl = canvas.ToView(box.TopLeft);
                var onScreen = new Rect(tl.X, tl.Y, box.Width * canvas.Zoom, box.Height * canvas.Zoom);
                Assert.That(starters.Intersects(onScreen), Is.False, $"{where}: the starters {starters} over {m.Name} {onScreen}; canvas {canvas.Bounds} offset {canvas.Offset} zoom {canvas.Zoom}; margin {view.Starters.Margin}");
            }
            Assert.That(new Rect(canvas.Bounds.Size).Contains(starters.TopLeft) && new Rect(canvas.Bounds.Size).Inflate(0.5).Contains(starters.BottomRight), Is.True, $"{where}: inside the view");
        }

        [AvaloniaTest]
        public void TheStartersGoToTheNearestFreePlaceOrDropTheirHintOrStayHidden()
        {
            var size = new Size(300, 100);
            var middle = new Rect(300, 150, 100, 60);
            // Free middle.
            Assert.That(MachineView.FreeSpot(size, 700, 400, new List<Rect>())!.Value, Is.EqualTo(new Point(200, 150)));
            // A box in the middle: beside or above or below it, never over it.
            var spot = MachineView.FreeSpot(size, 700, 400, new List<Rect> { middle.Inflate(MachineView.StartersGap) })!.Value;
            Assert.That(new Rect(spot, size).Intersects(middle.Inflate(MachineView.StartersGap)), Is.False);
            // No place at all.
            Assert.That(MachineView.FreeSpot(size, 320, 120, new List<Rect> { new Rect(0, 0, 320, 120) }), Is.Null);
        }

        // ------------------------------------------------------------------ a lit box

        [AvaloniaTest]
        public void ALitBoxKeepsItsNameReadableAtAFullLevel()
        {
            var text = StudioTokens.TextPrimary;
            double alpha = MachineCanvas.VeilAlpha / 255.0;
            foreach (MachineCategory c in Enum.GetValues(typeof(MachineCategory)))
            {
                var body = MachineIdentity.BodyOf(StudioTokens.Identity(c), false);
                byte Over(byte b) => (byte)Math.Round(b * (1 - alpha) + 255 * alpha);
                var lit = Color.FromRgb(Over(body.R), Over(body.G), Over(body.B));
                Assert.That(StudioTokens.Contrast(text, lit), Is.GreaterThanOrEqualTo(4.5), c + ": the name on its box at 0 dB");
            }
        }

        // ------------------------------------------------------------------ Export Audio

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
            var until = DateTime.UtcNow.AddSeconds(120);
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

        /// <summary>A song with Neo Drums playing a few bars (a drum machine renders the same each time; Neo Poly and Neo FM6 drift a little from render to render, so they would not do for byte comparisons).</summary>
        static MainWindow SongWithANote(int beats = 8, string gear = "Neo Drums")
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            Call("machine_add", Args(("gear", gear), ("name", "Exported")));
            Call("pattern_create", Args(("machine", "Exported"), ("name", "P"), ("length_beats", beats)));
            var notes = new JsonArray();
            for (int b = 0; b < beats; b += 2) notes.Add(new JsonObject { ["pitch"] = 36 + (b / 2) % 4, ["start"] = (double)b, ["length"] = 1.0, ["velocity"] = 100 });
            Call("notes_write", Args(("machine", "Exported"), ("pattern", "P"), ("notes", notes)));
            Call("arrangement_place", Args(("machine", "Exported"), ("pattern", "P"), ("at", 0)));
            Settle();
            return window;
        }

        static string TempFolder(string name) => TestTemp.Path("ux-p3j-" + name + "-" + Environment.ProcessId);

        static void Run(Task task) => Wait(task);

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
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [AvaloniaTest]
        public void ExportAudioHasItsKeyItsMenuEntryAndTheSettingsTheSpecSays()
        {
            var window = SongWithANote();
            string folder = TempFolder("sheet");
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            var command = StudioKeymap.Get("file.export");
            Assert.That(command.Gesture(false)!.ToString(), Is.EqualTo("Ctrl+Shift+R"));
            Assert.That(command.Gesture(true)!.ToString(), Is.EqualTo("Shift+Cmd+R"));
            Assert.That(StudioKeymap.Commands.Count(c => c.Id != command.Id && c.Gesture(false)?.ToString() == "Ctrl+Shift+R"), Is.EqualTo(0), "the key is free");
            Assert.That(window.KeyBindings.Any(k => k.Gesture.Key == Key.R && k.Gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)), Is.True, "bound in the window");

            var sheet = new ExportSheet(Core);
            sheet.Show();
            Settle();
            try
            {
                Assert.That(sheet.Bits.SelectedIndex, Is.EqualTo(1), "24 bit first");
                Assert.That(sheet.Dither.IsChecked, Is.False);
                Assert.That(sheet.Normalise.IsChecked, Is.False, "normalise is off");
                Assert.That(sheet.Stems.SelectedIndex, Is.EqualTo(0));
                sheet.Bits.SelectedIndex = 0;
                Assert.That(sheet.Dither.IsChecked, Is.True, "dither is on for 16 bit");
                Assert.That(sheet.Arguments()["dither"]!.GetValue<string>(), Is.EqualTo("tpdf"));
                sheet.Bits.SelectedIndex = 2;
                Assert.That(sheet.Dither.IsEnabled, Is.False, "32 bit float is written as it is");
                Assert.That(sheet.Arguments().ContainsKey("dither"), Is.False);
                Assert.That(sheet.Arguments()["bits"]!.GetValue<int>(), Is.EqualTo(32));
                Assert.That(sheet.Range.SelectedIndex, Is.EqualTo(0));
                Studio.Screenshot(sheet, "export-sheet");
            }
            finally
            {
                sheet.Close();
            }
        }

        [AvaloniaTest]
        public void TheSheetWritesTheBytesExportAudioWritesWithTheSameSettings()
        {
            var window = SongWithANote();
            string folder = TempFolder("same");
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            string opened = null;
            ExportSheet.FolderOpener = f => opened = f;
            var sheet = new ExportSheet(Core);
            sheet.Show();
            Settle();
            sheet.Bits.SelectedIndex = 0;                       // 16 bit with dither
            sheet.Rate.SelectedIndex = 1;                       // 44100
            sheet.Normalise.IsChecked = true;
            sheet.Stems.SelectedIndex = 1;                      // generators
            string viaSheet = Path.Combine(folder, "sheet.wav");
            sheet.PathBox.Text = viaSheet;
            var args = sheet.Arguments();
            var run = sheet.Run();
            Wait(run);
            Assert.That(run.Result, Is.True, sheet.Status.Text);
            Assert.That(opened, Is.EqualTo(folder), "the folder is opened when it is done");
            Assert.That(sheet.Result!["sample_rate"]!.GetValue<int>(), Is.EqualTo(44100));

            string viaTool = Path.Combine(folder, "tool.wav");
            args["path"] = viaTool;
            var r = Call("export_audio", args);
            Assert.That(r["files"]!.AsArray().Count, Is.EqualTo(2), "the master and Exported's stem");
            Assert.That(File.ReadAllBytes(viaSheet), Is.EqualTo(File.ReadAllBytes(viaTool)), "bit-identical to export_audio");
            Assert.That(File.ReadAllBytes(Path.Combine(folder, "sheet-Exported.wav")), Is.EqualTo(File.ReadAllBytes(Path.Combine(folder, "tool-Exported.wav"))), "and its stem");
            var back = ReBuzz.Agent.Audio.Wav.Read(viaSheet);
            Assert.That(back.Rate, Is.EqualTo(44100));
            Assert.That(20 * Math.Log10(Math.Max(back.Left.Max(Math.Abs), back.Right.Max(Math.Abs))), Is.EqualTo(-1).Within(0.1), "normalised to -1 dBFS");
        }

        [AvaloniaTest]
        public void TheLoopCanBeExportedAndTheSongIsLeftAsItWas()
        {
            var window = SongWithANote(16);
            string folder = TempFolder("loop");
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            ExportSheet.FolderOpener = _ => { };
            var song = Core.SongCore;
            song.LoopStart = 0;
            song.LoopEnd = 4 * Core.TPB;                       // one bar
            Core.Modified = false;
            int end = song.SongEnd;
            var sheet = new ExportSheet(Core);
            sheet.Show();
            Settle();
            sheet.Range.SelectedIndex = 1;
            sheet.Stems.SelectedIndex = 0;
            sheet.PathBox.Text = Path.Combine(folder, "loop.wav");
            var run = sheet.Run();
            Wait(run);
            Assert.That(run.Result, Is.True, sheet.Status.Text);
            double bar = 4 * 60.0 / Core.BPM;
            Assert.That(sheet.Result!["seconds"]!.GetValue<double>(), Is.EqualTo(bar + 2).Within(0.25), "a bar and the two second tail, not the whole song");
            Assert.That(song.SongEnd, Is.EqualTo(end), "the song is as it was");
            Assert.That(song.LoopEnd, Is.EqualTo(4 * Core.TPB));
            Assert.That(Core.Playing, Is.False);
            Assert.That(Core.Modified, Is.False, "rendering is not an edit");
        }

        [AvaloniaTest]
        public void CancellingStopsTheExportAndLeavesNoFile()
        {
            var window = SongWithANote(16);
            string folder = TempFolder("cancel");
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            bool opened = false;
            ExportSheet.FolderOpener = _ => opened = true;
            var sheet = new ExportSheet(Core);
            sheet.Show();
            Settle();
            sheet.Stems.SelectedIndex = 1;
            string path = Path.Combine(folder, "cancelled.wav");
            sheet.PathBox.Text = path;
            int reports = 0;
            bool barShown = false;
            ExportSheet.OnProgress = seconds =>
            {
                reports++;
                barShown = sheet.Bar.IsVisible;
                if (reports == 1) sheet.CancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            };
            var run = sheet.Run();
            Wait(run);
            Assert.That(run.Result, Is.False);
            Assert.That(barShown, Is.True, "the progress bar was there");
            Assert.That(reports, Is.GreaterThanOrEqualTo(1));
            Assert.That(sheet.Status.Text, Does.StartWith("Cancelled"));
            Assert.That(Directory.GetFiles(folder), Is.Empty, "nothing is left behind");
            Assert.That(opened, Is.False);
            Assert.That(sheet.IsVisible, Is.True, "the sheet stays, to change a setting or try again");
            Assert.That(sheet.Running, Is.False);
            Assert.That(Core.Playing, Is.False, "the song is stopped as it was");
            // And it works again after a cancel.
            ExportSheet.OnProgress = null;
            ExportSheet.FolderOpener = _ => { };
            var again = sheet.Run();
            Wait(again);
            Assert.That(again.Result, Is.True, sheet.Status.Text);
            Assert.That(File.Exists(path), Is.True);
        }

        [AvaloniaTest]
        public void AnExistingFileIsNeverReplacedWithoutAskingAndKeepingIsAnAnswer()
        {
            var window = SongWithANote();
            string folder = TempFolder("overwrite");
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            ExportSheet.FolderOpener = _ => { };
            string path = Path.Combine(folder, "keep.wav");
            File.WriteAllText(path, "mine");
            var sheet = new ExportSheet(Core);
            sheet.Show();
            Settle();
            sheet.PathBox.Text = path;
            var asked = new List<string>();
            Dialogs.Responder = (m, c, b) => { asked.Add(m); return UserAnswer.No; };
            var no = sheet.Run();
            Wait(no);
            Assert.That(no.Result, Is.False);
            Assert.That(asked.Count, Is.EqualTo(1), "it asked");
            Assert.That(asked[0], Does.Contain("keep.wav"));
            Assert.That(File.ReadAllText(path), Is.EqualTo("mine"), "Keep: the file is untouched");
            Assert.That(sheet.IsVisible, Is.True);

            // The tool itself refuses too (the agent's way), whatever the sheet does.
            var refused = Try("export_audio", Args(("path", path)));
            Assert.That(refused.Error, Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("mine"));

            Dialogs.Responder = (m, c, b) => UserAnswer.Yes;
            var yes = sheet.Run();
            Wait(yes);
            Assert.That(yes.Result, Is.True, sheet.Status.Text);
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(1000), "Replace: the export is there");
        }
    }
}
