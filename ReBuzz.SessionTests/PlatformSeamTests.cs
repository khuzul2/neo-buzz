using System.Collections.Concurrent;
using System.Diagnostics;
using ReBuzz.Core;
using ReBuzz.Platform;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The engine's seams for hosts without Windows or a UI toolkit (docs/specs/mac-engine.md M0):
    /// the registry in a JSON file, a dispatcher loop with timers, messages as lines.
    /// </summary>
    [TestFixture]
    public class PlatformSeamTests
    {
        const string Root = @"Software\ReBuzz";
        string folder = "";

        [SetUp]
        public void Folder() => folder = Directory.CreateTempSubdirectory("rebuzz-registry-").FullName;

        [TearDown]
        public void Clean()
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        string File1 => Path.Combine(folder, "settings", "registry.json");

        enum Mode { Off, Low, High }

        // ------------------------------------------------------------------ JsonRegistry

        [Test]
        public void ValuesRoundTripThroughTheFile()
        {
            var r = new JsonRegistry(File1, Root);
            r.Write("SampleRate", 48000, "ASIO");
            r.Write("Multithreading", true, @"BuzzGUI\EngineSettings");
            r.Write("AudioDriver", "CoreAudio", "Settings");
            r.Write("Gain", 0.75, "Settings");
            r.Write("ProcessorAffinity", 65535u, "Settings");
            r.Write("Mode", Mode.High, "Settings");

            var again = new JsonRegistry(File1, Root);   // read from the file, not the cache
            Assert.That(again.Read("SampleRate", 0, "ASIO"), Is.EqualTo(48000));
            Assert.That(again.Read("Multithreading", false, @"BuzzGUI\EngineSettings"), Is.True);
            Assert.That(again.Read("AudioDriver", "", "Settings"), Is.EqualTo("CoreAudio"));
            Assert.That(again.Read("Gain", 0.0, "Settings"), Is.EqualTo(0.75));
            Assert.That(again.Read("ProcessorAffinity", 0u, "Settings"), Is.EqualTo(65535u));
            Assert.That(again.Read("Mode", Mode.Off, "Settings"), Is.EqualTo(Mode.High));
            Assert.That(again.Read("SampleRate", "", "ASIO"), Is.EqualTo("48000"), "any value reads as text");
        }

        [Test]
        public void MissingOrUnreadableValuesGiveTheDefault()
        {
            var r = new JsonRegistry(File1, Root);
            Assert.That(r.Read("Nothing", 7, "Settings"), Is.EqualTo(7));
            r.Write("Driver", "CoreAudio", "Settings");
            Assert.That(r.Read("Driver", 42, "Settings"), Is.EqualTo(42), "text that isn't a number");
            Assert.That(r.Read("driver", "", "SETTINGS"), Is.EqualTo("CoreAudio"), "names are case-insensitive, as the registry's");
        }

        [Test]
        public void SubkeysTakeFullPathsAsCallersBuildThem()
        {
            var r = new JsonRegistry(File1, Root);
            var key = r.CreateCurrentUserSubKey(Root + @"\Recent File List");
            key.SetValue("File1", "/songs/a.bmx");
            key.SetValue("File2", "/songs/b.bmx");
            key.SetValue("File4", "/songs/d.bmx");
            Assert.That(r.ReadNumberedList<string>("File", "Recent File List"), Is.EqualTo(new[] { "/songs/a.bmx", "/songs/b.bmx" }), "up to the first gap");

            r.DeleteCurrentUserSubKey(Root + @"\Recent File List");
            Assert.That(r.ReadNumberedList<string>("File", "Recent File List"), Is.Empty);

            r.Write("MidiController1", "x", "Settings");
            r.Write("MidiController2", "y", "Settings");
            r.DeleteCurrentUserValue("MidiController1", Root + "Settings");   // MidiControllerAssignments builds it without a backslash
            Assert.That(r.ReadDictionary("Settings").Keys, Is.EquivalentTo(new[] { "MidiController2" }));
        }

        [Test]
        public void ADictionaryGivesNumbersAsNumbers()
        {
            var r = new JsonRegistry(File1, Root);
            var inputs = r.CreateCurrentUserSubKey(Root + @"\MIDI In List");
            inputs.SetValue("IAC Driver Bus 1", 1);
            inputs.SetValue("Keystation", "0");
            var d = new JsonRegistry(File1, Root).ReadDictionary("MIDI In List");
            Assert.That(d["IAC Driver Bus 1"] is int one && one == 1, Is.True, "a DWORD comes back as int");
            Assert.That(d["Keystation"], Is.EqualTo("0"));
        }

        [Test]
        public void ADamagedFileStartsAfreshAndIsReplaced()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File1)!);
            File.WriteAllText(File1, "{ not json");
            var r = new JsonRegistry(File1, Root);
            Assert.That(r.Read("SampleRate", 44100, "ASIO"), Is.EqualTo(44100));
            r.Write("SampleRate", 96000, "ASIO");
            Assert.That(new JsonRegistry(File1, Root).Read("SampleRate", 0, "ASIO"), Is.EqualTo(96000));
            Assert.That(File.Exists(File1 + ".tmp"), Is.False, "written through a temporary file, then moved");
        }

        [Test]
        public void TheSettingsFolderIsThePlatformsOwn()
        {
            string folder = JsonRegistry.SettingsFolder;
            if (OperatingSystem.IsMacOS()) Assert.That(folder, Does.EndWith(Path.Combine("Library", "Application Support", "ReBuzz")));
            else if (OperatingSystem.IsWindows()) Assert.That(folder, Does.EndWith("ReBuzz"));
            else Assert.That(folder, Does.EndWith("rebuzz"));
            Assert.That(JsonRegistry.DefaultPath, Is.EqualTo(Path.Combine(folder, "registry.json")));
        }

        // ------------------------------------------------------------------ LoopDispatcher

        [Test]
        public void HigherPrioritiesRunFirstAndEachInOrder()
        {
            using var d = new LoopDispatcher();
            var ran = new List<string>();
            d.BeginInvoke(() => ran.Add("normal 1"));
            d.BeginInvoke(() => ran.Add("background"), UiPriority.Background);
            d.BeginInvoke(() => ran.Add("send"), UiPriority.Send);
            d.BeginInvoke(() => ran.Add("normal 2"));
            d.BeginInvoke(d.Stop, UiPriority.SystemIdle);
            d.Run();
            Assert.That(ran, Is.EqualTo(new[] { "send", "normal 1", "normal 2", "background" }));
        }

        [Test]
        public void InvokeRunsOnTheLoopAndWaitsForIt()
        {
            using var d = new LoopDispatcher();
            d.Start();
            int loopThread = -1, ranOn = -1;
            d.Invoke(() => loopThread = Environment.CurrentManagedThreadId);
            Assert.That(loopThread, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
            Assert.That(d.CheckAccess(), Is.False, "the test thread isn't the loop");
            d.Invoke(() =>
            {
                Assert.That(d.CheckAccess(), Is.True);
                d.Invoke(() => ranOn = Environment.CurrentManagedThreadId);   // on the loop: at once, no deadlock
            });
            Assert.That(ranOn, Is.EqualTo(loopThread));
            var e = Assert.Throws<InvalidOperationException>(() => d.Invoke(() => throw new FormatException("bad")));
            Assert.That(e!.InnerException is FormatException, Is.True);
        }

        [Test]
        public void AFailingActionIsReportedAndTheLoopGoesOn()
        {
            using var d = new LoopDispatcher();
            var errors = new ConcurrentQueue<Exception>();
            d.UnhandledException += errors.Enqueue;
            d.Start();
            d.BeginInvoke(() => throw new InvalidDataException("boom"));
            using var after = new ManualResetEventSlim();
            d.BeginInvoke(after.Set);                          // queued behind it, at the same priority
            Assert.That(after.Wait(5000), Is.True, "the next action ran");
            Assert.That(errors.Single() is InvalidDataException, Is.True);
        }

        [Test]
        public void TimersTickOnTheLoopUntilStopped()
        {
            using var d = new LoopDispatcher();
            d.Start();
            var ticks = new ConcurrentQueue<int>();
            var timer = d.CreateTimer(TimeSpan.FromMilliseconds(10), () => ticks.Enqueue(Environment.CurrentManagedThreadId));
            Assert.That(timer.IsEnabled, Is.False, "starts stopped");
            int loop = -1;
            d.Invoke(() => loop = Environment.CurrentManagedThreadId);
            timer.Start();
            var clock = Stopwatch.StartNew();
            while (ticks.Count < 5 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            timer.Stop();
            Assert.That(ticks.Count, Is.GreaterThanOrEqualTo(5));
            Assert.That(ticks.Distinct(), Is.EqualTo(new[] { loop }), "every tick on the loop's thread");
            Thread.Sleep(30);
            int stopped = ticks.Count;
            Thread.Sleep(60);
            Assert.That(ticks.Count, Is.EqualTo(stopped), "no ticks after Stop");
        }

        [Test]
        public void ASlowLoopDoesntPileUpTicks()
        {
            using var d = new LoopDispatcher();
            d.Start();
            int ticks = 0;
            var timer = d.CreateTimer(TimeSpan.FromMilliseconds(1), () => Interlocked.Increment(ref ticks));
            timer.Start();
            d.Invoke(() => Thread.Sleep(100));                  // the loop is busy for 100 timer periods
            Thread.Sleep(5);
            timer.Stop();
            Assert.That(ticks, Is.LessThan(20), "a waiting tick isn't queued again");
        }

        // ------------------------------------------------------------------ ConsoleUserMessages

        [Test]
        public void MessagesAreLinesAndQuestionsDoTheLeast()
        {
            var output = new StringWriter();
            var m = new ConsoleUserMessages(output);
            m.Error("Can't open\nthe device", "Audio", new IOException("busy"));
            Assert.That(m.Ask("Save changes?", "ReBuzz", UserButtons.YesNoCancel), Is.EqualTo(UserAnswer.Cancel));
            Assert.That(m.Ask("Load it safely?", "Machine", UserButtons.YesNo), Is.EqualTo(UserAnswer.No));
            Assert.That(m.Ask("Done.", "ReBuzz", UserButtons.Ok), Is.EqualTo(UserAnswer.Ok));
            Assert.That(m.KeepWaiting("Still loading", "Machines"), Is.True);
            var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(lines[0], Is.EqualTo("[ReBuzz] error: Audio: Can't open the device (IOException: busy)"));
            Assert.That(lines[1], Is.EqualTo("[ReBuzz] question: ReBuzz: Save changes? → Cancel"));
            Assert.That(lines.Length, Is.EqualTo(5));
        }
    }
}
