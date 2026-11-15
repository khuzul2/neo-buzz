// Fork (docs/specs/plugin-host.md H1b, H3): the studio's side of plug-in hosting, headless: a plug-in editor's window policy (one window
// per machine, embedded, the floating fallback, close and reopen, no editor on Linux), the native control host's sizing (the plug-in's
// size and the display scale, the plug-in's own resize requests, the window's resizing), and Preferences ▸ Plug-ins (folders, rescan,
// the blocklist and its persistence). The embedding itself needs a desktop: the Windows and macOS jobs run it with a real plug-in
// (ReBuzz.Studio --plugin-gui-check, tools/ci-plugin-host.sh).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Platform;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Plug;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using Avalonia.Threading;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell.Preferences;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxP4aTests;

namespace ReBuzz.Studio.Tests
{
    public class PluginHostUiTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void SettleWindow(Window window)
        {
            Settle();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static IMachine M(string name) => Core.SongCore.Machines.First(m => m.Name == name);

        // ------------------------------------------------------------------ fakes

        sealed class FakeSession : INativeEditorSession
        {
            public IntPtr Handle { get; set; } = new IntPtr(77);
            public bool Floating { get; set; }
            public int Width { get; set; } = 800;
            public int Height { get; set; } = 600;
            public bool CanResize { get; set; } = true;
            public List<(int W, int H)> SetSizes = new();
            public List<double> Scales = new();
            public bool Disposed;
            public event Action<int, int> Resized;
            public event Action CloseRequested;
            public void SetSize(int w, int h) { SetSizes.Add((w, h)); Width = w; Height = h; }
            public void SetScale(double s) => Scales.Add(s);
            public string Snapshot(string imagePath) => null;
            public void Dispose() => Disposed = true;
            public void RaiseResized(int w, int h) { Width = w; Height = h; Resized?.Invoke(w, h); }
            public void RaiseClose() => CloseRequested?.Invoke();
        }

        sealed class FakeEditorMachine : IBuzzMachine, IBuzzMachineNativeEditor
        {
            public int Support = 3;
            public FakeSession Last;
            public int Opens;
            public bool Fails;
            public double LastScale;
            public int EditorSupport(string api) => Support;

            public INativeEditorSession OpenEditor(string api, bool floating, IntPtr parent, double scale, string title, out string error)
            {
                Opens++;
                LastScale = scale;
                if (Fails)
                {
                    error = "the plug-in refused its parent";
                    return null;
                }
                error = null;
                return Last = new FakeSession { Floating = floating };
            }
        }

        sealed class FakeWindow : IPluginEditorWindow
        {
            public bool Open = true;
            public int Raised;
            public bool IsOpen => Open;
            public INativeEditorSession Session => null;
            public event Action Closed;
            public void Raise() => Raised++;
            public void Close()
            {
                if (!Open) return;
                Open = false;
                Closed?.Invoke();
            }
        }

        sealed class FakePlatform : IPluginEditorPlatform
        {
            public string Api { get; set; } = "hwnd";
            public int EmbedFailures;        // the next this-many embeds fail
            public int EmbedCalls, FloatCalls;
            public bool FloatWorks = true;
            public List<FakeWindow> Windows = new();

            public IPluginEditorWindow ShowEmbedded(IMachine machine, IBuzzMachineNativeEditor editor, string title, WindowPlacement? at, out string error)
            {
                EmbedCalls++;
                if (EmbedFailures > 0)
                {
                    EmbedFailures--;
                    error = "the control could not be made";
                    return null;
                }
                error = null;
                var w = new FakeWindow();
                Windows.Add(w);
                return w;
            }

            public IPluginEditorWindow ShowFloating(IMachine machine, IBuzzMachineNativeEditor editor, string title, out string error)
            {
                FloatCalls++;
                if (!FloatWorks)
                {
                    error = "no window";
                    return null;
                }
                error = null;
                var w = new FakeWindow();
                Windows.Add(w);
                return w;
            }
        }

        /// <summary>A machine of the open song that answers as a plug-in with an editor (its managed machine swapped for the fake).</summary>
        static FakeEditorMachine WithEditor(string name, int support = 3)
        {
            var m = (MachineCore)M(name);
            var fake = new FakeEditorMachine { Support = support };
            m.ManagedMachine = fake;
            return fake;
        }

        // ------------------------------------------------------------------ the window policy

        [AvaloniaTest]
        public void AnEditorOpensEmbeddedOncePerMachineAndTheNextDoubleClickRaisesIt()
        {
            var window = SongWith("Neo Poly", 8);
            var real = ((MachineCore)M("Voice")).ManagedMachine;
            using var restore = Studio.AfterTest(() => { ((MachineCore)M("Voice")).ManagedMachine = real; Restore(window, null); });
            WithEditor("Voice");
            var p = new FakePlatform();
            var c = new PluginEditorController(p);
            Assert.That(c.Has(M("Voice")), Is.True);
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Embedded));
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Embedded));
            Assert.That(p.EmbedCalls, Is.EqualTo(1), "one window per instance: the second double click opens nothing");
            Assert.That(p.Windows[0].Raised, Is.EqualTo(1), "it raises the open one");
            Assert.That(c.OpenCount, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void ClosingTheWindowForgetsItAndTheNextDoubleClickOpensItAgain()
        {
            var window = SongWith("Neo Poly", 8);
            var real = ((MachineCore)M("Voice")).ManagedMachine;
            using var restore = Studio.AfterTest(() => { ((MachineCore)M("Voice")).ManagedMachine = real; Restore(window, null); });
            WithEditor("Voice");
            var p = new FakePlatform();
            var c = new PluginEditorController(p);
            c.Open(M("Voice"));
            p.Windows[0].Close();
            Assert.That(c.ModeOf(M("Voice")), Is.EqualTo(PluginEditorMode.None));
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Embedded));
            Assert.That(p.EmbedCalls, Is.EqualTo(2), "reopened");
            c.Close(M("Voice"));
            Assert.That(p.Windows[1].IsOpen, Is.False, "the machine's deletion closes it");
            Assert.That(c.OpenCount, Is.EqualTo(0));
        }

        [AvaloniaTest]
        public void WhenEmbeddingFailsTwiceTheEditorFloatsAndEmbeddingIsNotTriedAgainForThatMachine()
        {
            var window = SongWith("Neo Poly", 8);
            var real = ((MachineCore)M("Voice")).ManagedMachine;
            using var restore = Studio.AfterTest(() => { ((MachineCore)M("Voice")).ManagedMachine = real; Restore(window, null); });
            WithEditor("Voice");
            var p = new FakePlatform { EmbedFailures = 5 };
            var c = new PluginEditorController(p);
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Floating));
            Assert.That(p.EmbedCalls, Is.EqualTo(PluginEditorController.MaxAttempts), "two real attempts");
            Assert.That(p.FloatCalls, Is.EqualTo(1));
            Assert.That(c.LastError, Is.EqualTo("the control could not be made"));
            p.Windows.Last().Close();
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Floating));
            Assert.That(p.EmbedCalls, Is.EqualTo(2), "it floats straight away the next time");
        }

        [AvaloniaTest]
        public void AFloatingOnlyEditorOrSwitchedOffEmbeddingFloatsWithoutTryingToEmbed()
        {
            var window = SongWith("Neo Poly", 8);
            var real = ((MachineCore)M("Voice")).ManagedMachine;
            using var restore = Studio.AfterTest(() => { ((MachineCore)M("Voice")).ManagedMachine = real; Restore(window, null); });
            var fake = WithEditor("Voice", support: 2);
            var p = new FakePlatform();
            var c = new PluginEditorController(p);
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Floating));
            Assert.That(p.EmbedCalls, Is.EqualTo(0));
            c.Close(M("Voice"));
            fake.Support = 3;
            c.EmbeddingOn = false;      // NEOBUZZ_PLUGIN_GUI=floating
            Assert.That(c.Open(M("Voice")), Is.EqualTo(PluginEditorMode.Floating));
            Assert.That(p.EmbedCalls, Is.EqualTo(0));
        }

        [AvaloniaTest]
        public void WithoutAnEditorOrAWindowApiTheControllerOpensNothingAndTheDeviceViewIsUsed()
        {
            var window = SongWith("Neo Poly", 8);
            var real = ((MachineCore)M("Voice")).ManagedMachine;
            var openerBefore = StudioMachineUiHost.DeviceOpener;
            var controllerBefore = StudioMachineUiHost.PluginEditors;
            using var restore = Studio.AfterTest(() =>
            {
                ((MachineCore)M("Voice")).ManagedMachine = real;
                StudioMachineUiHost.DeviceOpener = openerBefore;
                StudioMachineUiHost.PluginEditors = controllerBefore;
                Restore(window, null);
            });
            // Linux: no window API; a plug-in with no editor; a machine that is not a plug-in at all.
            var fake = WithEditor("Voice", support: 0);
            var none = new FakePlatform { Api = null };
            Assert.That(new PluginEditorController(none).Open(M("Voice")), Is.EqualTo(PluginEditorMode.None));
            var p = new FakePlatform();
            Assert.That(new PluginEditorController(p).Open(M("Voice")), Is.EqualTo(PluginEditorMode.None), "the plug-in has no editor");
            ((MachineCore)M("Voice")).ManagedMachine = real;
            Assert.That(new PluginEditorController(p).Open(M("Voice")), Is.EqualTo(PluginEditorMode.None), "not a plug-in");
            Assert.That(p.EmbedCalls + p.FloatCalls, Is.EqualTo(0));

            // The studio's host: an editor first, else the device view as before.
            StudioMachineUiHost.PluginEditors = new PluginEditorController(p);
            bool deviceOpened = false;
            StudioMachineUiHost.DeviceOpener = m => deviceOpened = true;
            Core.UiHost.OpenGui(M("Voice"), null);
            Assert.That(p.EmbedCalls, Is.EqualTo(0));
            Assert.That(deviceOpened || StudioMachineUiHost.GuiWindowOf(M("Voice")) != null || StudioMachineUiHost.ParameterWindowOf(M("Voice")) != null, Is.True,
                "a machine with no plug-in editor opens what it did before");
            fake.Support = 3;
            ((MachineCore)M("Voice")).ManagedMachine = fake;
            deviceOpened = false;
            Core.UiHost.OpenGui(M("Voice"), null);
            Assert.That(p.EmbedCalls, Is.EqualTo(1), "a double click on a plug-in opens its editor");
            Assert.That(deviceOpened, Is.False, "and not the device view");
            Core.UiHost.CloseWindows(M("Voice"));
            Assert.That(p.Windows[0].IsOpen, Is.False, "closing a machine's windows closes its editor");
        }

        // ------------------------------------------------------------------ the native control host

        static IPlatformHandle MakeNativeControl(PluginEditorHost host)
        {
            var m = typeof(PluginEditorHost).GetMethod("CreateNativeControlCore", BindingFlags.Instance | BindingFlags.NonPublic);
            return (IPlatformHandle)m.Invoke(host, new object[] { new PlatformHandle(new IntPtr(5), "HWND") });
        }

        static void DestroyNativeControl(PluginEditorHost host, IPlatformHandle handle)
        {
            var m = typeof(PluginEditorHost).GetMethod("DestroyNativeControlCore", BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(host, new object[] { handle });
        }

        [AvaloniaTest]
        public void TheHostTakesTheViewsSizeAndFollowsThePlugInsOwnResizeRequests()
        {
            var editor = new FakeEditorMachine();
            var host = new PluginEditorHost(editor, "hwnd", "Plug");
            int refits = 0;
            host.ContentSizeChanged += () => refits++;
            host.Measure(new Size(2000, 2000));
            Assert.That(host.DesiredSize, Is.EqualTo(PluginEditorHost.DefaultSize), "before it is open, a size most editors fit in");
            var handle = MakeNativeControl(host);
            Assert.That(handle.Handle, Is.EqualTo(editor.Last.Handle), "the container the plug-in attached to is the native control");
            Assert.That(host.Session, Is.SameAs(editor.Last));
            host.Measure(new Size(2000, 2000));
            // Windows counts in physical pixels, macOS in points; the headless platform has a scale of 1 either way.
            Assert.That(host.DesiredSize, Is.EqualTo(new Size(800, 600)));
            editor.Last.RaiseResized(1024, 700);
            host.Measure(new Size(2000, 2000));
            Assert.That(host.DesiredSize, Is.EqualTo(new Size(1024, 700)), "the plug-in asked for another size");
            Assert.That(refits, Is.EqualTo(1), "the studio window refits");
            DestroyNativeControl(host, handle);
            Assert.That(editor.Last.Disposed, Is.True, "closing the window detaches the plug-in and releases its container");
            Assert.That(host.Session, Is.Null);
        }

        [AvaloniaTest]
        public void AWindowTheUserResizesPassesItsSizeToThePlugInWhenItMayBeResized()
        {
            var editor = new FakeEditorMachine();
            var host = new PluginEditorHost(editor, "hwnd", "Plug");
            var handle = MakeNativeControl(host);
            host.Measure(new Size(2000, 2000));
            host.Arrange(new Rect(0, 0, 900, 650));
            Assert.That(editor.Last.SetSizes, Is.EqualTo(new[] { (900, 650) }), "the new size goes to the plug-in once");
            host.Arrange(new Rect(0, 0, 900, 650));
            Assert.That(editor.Last.SetSizes.Count, Is.EqualTo(1), "an unchanged size is not sent again");
            editor.Last.CanResize = false;
            host.Arrange(new Rect(0, 0, 500, 400));
            Assert.That(editor.Last.SetSizes.Count, Is.EqualTo(1), "a fixed-size editor is left alone");
            DestroyNativeControl(host, handle);
        }

        [AvaloniaTest]
        public void AnEditorThatCannotBeOpenedLeavesTheHostEmptyWithTheReason()
        {
            var editor = new FakeEditorMachine { Fails = true };
            var host = new PluginEditorHost(editor, "hwnd", "Plug");
            // The base class makes a blank default child where there is a real platform; the headless one has none to make it with.
            try { MakeNativeControl(host); }
            catch (TargetInvocationException) { }
            Assert.That(host.Session, Is.Null);
            Assert.That(host.Error, Is.EqualTo("the plug-in refused its parent"));
        }

        [AvaloniaTest]
        public void ThePlugInClosingItsEditorClosesTheStudioWindow()
        {
            var editor = new FakeEditorMachine();
            var host = new PluginEditorHost(editor, "hwnd", "Plug");
            bool closed = false;
            host.CloseRequested += () => closed = true;
            MakeNativeControl(host);
            editor.Last.RaiseClose();       // posted to the UI thread: the plug-in's host thread may be the one that says it
            Settle();
            Assert.That(closed, Is.True);
        }

        // ------------------------------------------------------------------ Preferences ▸ Plug-ins

        sealed class PluginData : IDisposable
        {
            public readonly string Dir, Plugins;
            readonly string oldDir, oldScan, oldStandard, oldPath;

            public PluginData(string name)
            {
                Dir = TestTemp.Path($"plugin-data-{name}-{Environment.ProcessId}");
                Plugins = TestTemp.Path($"plugin-folder-{name}-{Environment.ProcessId}");
                Directory.CreateDirectory(Dir);
                Directory.CreateDirectory(Plugins);
                oldDir = Environment.GetEnvironmentVariable("NEOBUZZ_PLUGIN_DIR");
                oldScan = Environment.GetEnvironmentVariable("NEOPLUG_SCAN");
                oldStandard = Environment.GetEnvironmentVariable("NEOBUZZ_PLUGIN_STANDARD");
                oldPath = Environment.GetEnvironmentVariable("NEOBUZZ_PLUGIN_PATH");
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_DIR", Dir);
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_STANDARD", "0");
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_PATH", null);
                PlugLibrary.Reload();
            }

            public void UseScanner(string path) => Environment.SetEnvironmentVariable("NEOPLUG_SCAN", path);

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_DIR", oldDir);
                Environment.SetEnvironmentVariable("NEOPLUG_SCAN", oldScan);
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_STANDARD", oldStandard);
                Environment.SetEnvironmentVariable("NEOBUZZ_PLUGIN_PATH", oldPath);
                PlugLibrary.Reload();
                try { Directory.Delete(Dir, true); } catch (Exception) { }
                try { Directory.Delete(Plugins, true); } catch (Exception) { }
            }
        }

        /// <summary>A stand-in for neoplug-scan: lists one effect for a module, but a name with "crash" in it exits abnormally and one with "bad" says it does not load.</summary>
        static string StubScanner(string folder)
        {
            string path = Path.Combine(folder, "stub-scan.sh");
            File.WriteAllText(path, "#!/bin/sh\n" +
                "case \"$1\" in\n" +
                "  *crash*) exit 139 ;;\n" +
                "  *bad*) echo '{\"path\":\"'\"$1\"'\",\"error\":\"missing a dependency\",\"classes\":[]}'; exit 1 ;;\n" +
                "esac\n" +
                "echo '{\"path\":\"'\"$1\"'\",\"classes\":[{\"format\":\"clap\",\"id\":\"stub.fx\",\"name\":\"Stub FX\",\"vendor\":\"Tests\",\"instrument\":false,\"params\":3,\"inputs\":2,\"outputs\":2}]}'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        static void WaitForScan(PlugLibrary.ScanReport before)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while ((PlugLibrary.Scanning || ReferenceEquals(PlugLibrary.LastReport, before)) && DateTime.UtcNow < until) System.Threading.Thread.Sleep(20);
        }

        [AvaloniaTest]
        public void ThePlugInsPageListsTheFoldersAndTakesAndForgetsOnesTheUserAdds()
        {
            using var data = new PluginData("folders");
            var prefs = PreferencesWindow.Open(Studio.Host, "Plug-ins");
            using var restore = Studio.AfterTest(() => prefs.Close());
            SettleWindow(prefs);
            var page = prefs.Page<PluginsPage>();
            Assert.That(prefs.Selected, Is.SameAs(page));
            Assert.That(page.FolderRows, Is.Empty, "the standard folders are switched off for the test and none was added");

            page.FolderBox.Text = Path.Combine(data.Plugins, "no-such-folder");
            page.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(page.StatusText, Does.Contain("isn't a folder"), "a folder that doesn't exist is refused");
            Assert.That(PlugPaths.UserFolders(), Is.Empty);

            page.FolderBox.Text = data.Plugins;
            page.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SettleWindow(prefs);
            Assert.That(page.FolderRows.Select(r => r.Path), Is.EqualTo(new[] { data.Plugins }));
            Assert.That(page.FolderRows[0].Custom, Is.True);
            Assert.That(File.ReadAllLines(Path.Combine(data.Dir, "folders.txt")), Is.EqualTo(new[] { data.Plugins }), "kept in the plug-in data folder");
            Assert.That(PlugPaths.SearchDirs(), Does.Contain(data.Plugins), "and scanned from now on");

            page.FolderRows[0].Remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SettleWindow(prefs);
            Assert.That(page.FolderRows, Is.Empty);
            Assert.That(PlugPaths.UserFolders(), Is.Empty);
        }

        [AvaloniaTest]
        public void RescanFindsPlugInsBlocksTheOnesThatCrashWithTheReasonAndUnblockingIsKept()
        {
            if (OperatingSystem.IsWindows()) Assert.Ignore("the stand-in scanner is a shell script");
            using var data = new PluginData("blocklist");
            data.UseScanner(StubScanner(data.Dir));
            File.WriteAllText(Path.Combine(data.Plugins, "good.clap"), "x");
            File.WriteAllText(Path.Combine(data.Plugins, "crashy.clap"), "x");
            File.WriteAllText(Path.Combine(data.Plugins, "bad.clap"), "x");
            PlugPaths.AddFolder(data.Plugins);
            var prefs = PreferencesWindow.Open(Studio.Host, "Plug-ins");
            using var restore = Studio.AfterTest(() => prefs.Close());
            SettleWindow(prefs);
            var page = prefs.Page<PluginsPage>();
            Assert.That(page.BlockedRows, Is.Empty);

            var lastReport = PlugLibrary.LastReport;
            page.RescanButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitForScan(lastReport);
            page.Refresh();
            SettleWindow(prefs);
            Assert.That(PlugLibrary.All().Select(e => e.Name), Is.EqualTo(new[] { "Stub FX" }), "the good module is on the Gear menu's list");
            Assert.That(page.BlockedRows.Select(r => Path.GetFileName(r.Path)).OrderBy(n => n), Is.EqualTo(new[] { "bad.clap", "crashy.clap" }));
            Assert.That(page.BlockedRows.First(r => r.Path.EndsWith("crashy.clap")).Reason, Does.Contain("crashed while scanning"));
            Assert.That(page.BlockedRows.First(r => r.Path.EndsWith("bad.clap")).Reason, Does.Contain("missing a dependency"));
            Assert.That(page.StatusText, Does.StartWith("Scan finished"));

            // The blocklist is kept: another read of the file (another process, the next start) finds the same.
            PlugLibrary.Reload();
            Assert.That(PlugLibrary.Blocked().Count, Is.EqualTo(2));

            // A plain rescan leaves blocked modules alone; unblocking one is kept and the next scan tries it again.
            page.BlockedRows.First(r => r.Path.EndsWith("bad.clap")).Unblock.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SettleWindow(prefs);
            Assert.That(page.BlockedRows.Select(r => Path.GetFileName(r.Path)), Is.EqualTo(new[] { "crashy.clap" }));
            PlugLibrary.Reload();
            Assert.That(PlugLibrary.Blocked().Select(b => Path.GetFileName(b.Path)), Is.EqualTo(new[] { "crashy.clap" }), "kept in library.json");

            page.UnblockAllButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SettleWindow(prefs);
            Assert.That(page.BlockedRows, Is.Empty);
            PlugLibrary.Reload();
            Assert.That(PlugLibrary.Blocked(), Is.Empty);
        }

        [AvaloniaTest]
        public void AChangeToTheLibraryFileMadeElsewhereIsSeenWithoutARestart()
        {
            using var data = new PluginData("reload");
            Assert.That(PlugLibrary.Blocked(), Is.Empty);
            File.WriteAllText(Path.Combine(data.Dir, "library.json"),
                "{\"Format\":1,\"Modules\":[],\"Blocked\":[{\"Path\":\"/x/y.vst3\",\"Size\":1,\"Modified\":2,\"Reason\":\"hung\",\"When\":\"\"}]}");
            File.SetLastWriteTimeUtc(Path.Combine(data.Dir, "library.json"), DateTime.UtcNow.AddMinutes(1));
            Assert.That(PlugLibrary.Blocked().Select(b => b.Reason), Is.EqualTo(new[] { "hung" }), "the machines' copy of the library follows the studio's page");
        }

        // The UI thread is the plug-ins' main thread (StudioPluginMainThread): its own calls run at once, other threads' calls queue to
        // it and wait, its timer ticks on it, and nothing waits for itself.
        [AvaloniaTest]
        public void TheUiThreadIsThePluginMainThread()
        {
            var main = new StudioPluginMainThread();
            int ui = Environment.CurrentManagedThreadId;
            Assert.That(main.IsCurrent, Is.True);
            Assert.That(main.Invoke(() => Environment.CurrentManagedThreadId, 1000), Is.EqualTo(ui), "on the UI thread a call runs inline");

            var other = System.Threading.Tasks.Task.Run(() =>
            {
                bool wasCurrent = main.IsCurrent;
                int ranOn = main.Invoke(() => Environment.CurrentManagedThreadId, 20000);
                return (wasCurrent, ranOn);
            });
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!other.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }
            Assert.That(other.IsCompleted, "a call from another thread is served while the UI thread runs its jobs");
            Assert.That(other.Result.wasCurrent, Is.False);
            Assert.That(other.Result.ranOn, Is.EqualTo(ui), "and runs on the UI thread");

            int posted = 0, ticks = 0, tickThread = 0;
            main.Post(() => posted++);
            Dispatcher.UIThread.RunJobs();
            Assert.That(posted, Is.EqualTo(1));
            using (main.Every(10, () => { ticks++; tickThread = Environment.CurrentManagedThreadId; }))
            {
                until = DateTime.UtcNow.AddSeconds(5);
                while (ticks < 3 && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }
            }
            Assert.That(ticks, Is.GreaterThanOrEqualTo(3), "the pump ticks");
            Assert.That(tickThread, Is.EqualTo(ui), "on the UI thread");
        }

        [AvaloniaTest]
        public void AWaitingCallGivesUpInsteadOfHanging()
        {
            var main = new StudioPluginMainThread();
            var other = System.Threading.Tasks.Task.Run(() =>
            {
                try { main.Invoke(() => 1, 200); return "answered"; }
                catch (TimeoutException) { return "timed out"; }
            });
            // The UI thread does not run its jobs for this long: the call must come back with a timeout, not wait for ever.
            Assert.That(other.Wait(10000), "the call returns");
            Assert.That(other.Result, Is.EqualTo("timed out"));
            Dispatcher.UIThread.RunJobs();
        }
    }
}
