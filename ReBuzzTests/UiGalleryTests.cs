using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/ui-gallery.md): the Windows app's half of the UI gallery. <c>NeoBuzzLive.exe --ui-shots</c> starts
    /// it as people do (main window, Gear with the natives, themes), loads the galleries' demo song and draws its
    /// windows: every view, the main menu's menus, the Machine View's context menus, both themes, Preferences and Settings
    /// (every page), About, the agent connection, rename, parameter windows, tool windows (win-*.png), with
    /// index-windows.md. They go where the other tests' screenshots go (SESSION_SCREENSHOT_DIR: the ci-artifacts branch).
    /// A shot that couldn't be taken is listed in the index and here, and doesn't fail the test; the main window's views must be there.
    /// </summary>
    public class UiGalleryTests
    {
        [Test]
        public void TheAppDrawsItsWindowsForTheUiGallery()
        {
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, ReBuzz.Product.WindowsExe);
            if (!File.Exists(exe)) Assert.Inconclusive($"{ReBuzz.Product.WindowsExe} not built ({exe})");
            string folder = Path.Combine(Path.GetTempPath(), "rebuzz-ui-shots-" + Environment.ProcessId);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);

            var psi = new ProcessStartInfo(exe, $"--ui-shots \"{folder}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = bin,
            };
            using var process = Process.Start(psi)!;
            var stderr = new List<string>();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.Add(e.Data); };
            process.OutputDataReceived += (_, e) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            bool exited = process.WaitForExit(TimeSpan.FromMinutes(10));
            if (!exited) process.Kill(true);
            string log = Path.Combine(folder, "ui-shots.log");
            TestContext.Out.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "(no ui-shots.log)");
            lock (stderr) TestContext.Out.WriteLine(string.Join("\n", stderr.TakeLast(60)));
            Assert.That(exited, Is.True, $"{ReBuzz.Product.WindowsExe} --ui-shots finished in 10 minutes");
            Assert.That(File.Exists(Path.Combine(folder, "done.txt")), Is.True, "the gallery finished (done.txt)");

            string shots = Environment.GetEnvironmentVariable("SESSION_SCREENSHOT_DIR") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(shots);
            foreach (var file in Directory.GetFiles(folder, "win-*.png").Concat(Directory.GetFiles(folder, "index-windows.md")))
                File.Copy(file, Path.Combine(shots, Path.GetFileName(file)), true);

            string index = File.ReadAllText(Path.Combine(folder, "index-windows.md"));
            int notCaptured = index.Split('\n').Count(l => l.StartsWith("- "));
            TestContext.Out.WriteLine($"[notice] UI gallery: {Directory.GetFiles(folder, "win-*.png").Length} shots, {notCaptured} not captured");
            foreach (var line in index.Split('\n').Where(l => l.StartsWith("- "))) TestContext.Out.WriteLine("[notice] not captured " + line.Substring(2));
            foreach (var view in new[] { "win-020-machines.png", "win-030-pattern.png", "win-040-sequence.png", "win-050-wavetable.png", "win-060-song-info.png", "win-070-session.png" })
                Assert.That(File.Exists(Path.Combine(folder, view)), Is.True, $"{view} drawn");
        }
    }
}
