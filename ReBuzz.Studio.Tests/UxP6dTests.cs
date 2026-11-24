// Fork (docs/specs/ux-overhaul.md P6c, §11 "P6c as built"): the switch. The WPF tests' app-level scenarios that the studio had
// no test for (§9 row 17): starting from an empty song, cancelling and failing to open, saving for the first time, again without a
// name, saving as, opening what was saved, the recent list; and the file types the studio registers for itself (Preferences ▸ General).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class UxP6dTests
    {
        static StudioHost Host => Studio.Host;
        static ReBuzzCore Core => Host.Core;

        static string Folder
        {
            get
            {
                string dir = TestTemp.Path("rebuzz-studio-p6c-" + System.Environment.ProcessId);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        static string NewPath() => Path.Combine(Folder, "song-" + System.Guid.NewGuid().ToString("N")[..8] + ".nbl");

        static void Settle() { Dispatcher.UIThread.RunJobs(); Dispatcher.UIThread.RunJobs(); }

        static void Fresh()
        {
            SongFiles.Picker = null;
            Dialogs.Responder = (m, c, b) => b == UserButtons.Ok ? UserAnswer.Ok : UserAnswer.No;   // never keep unsaved changes here
            Core.Modified = false;
            SongFiles.New().Wait();
            Settle();
            Core.Modified = false;
            RecentFiles.Clear(Host.Registry);
        }

        static void Reset()
        {
            SongFiles.Picker = null;
            Dialogs.Responder = null;
            GeneralPage.Associations = FileAssociations.ForThisApp;
            Core.Modified = false;
            Settle();
        }

        /// <summary>InitialStateTests: the state of a song with nothing in it (ReBuzzTests' InitialStateAssertions, the part the studio shows).</summary>
        static void AssertEmptySong(string name, bool checkModified = true)
        {
            Assert.That(Core.SongCore.SongName, Is.EqualTo(name), "the song's file");
            Assert.That(Core.BPM, Is.EqualTo(126));
            Assert.That(Core.TPB, Is.EqualTo(4));
            Assert.That(Core.Playing, Is.False);
            Assert.That(Core.Recording, Is.False);
            Assert.That(Core.Looping, Is.False);
            Assert.That(Core.SongCore.MachinesList.Select(m => m.Name), Is.EqualTo(new[] { "Master" }), "the Master alone");
            if (checkModified) Assert.That(Core.Modified, Is.False);
        }

        [AvaloniaTest]
        public void ANewSongIsTheInitialState()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            Core.BPM = 140;
            Core.SongCore.CreateMachine("Neo Poly", null, "Extra", null, null, null, -1, 0.3f, 0.1f);
            Core.Modified = true;

            SongFiles.New().Wait();     // the question is answered No by the responder: the changes are dropped
            Settle();

            AssertEmptySong(null, checkModified: false);     // (the New command leaves the flag to the engine's own bookkeeping; the shell's NewSong clears it)
        }

        [AvaloniaTest]
        public void CancellingOrPickingNothingChangesNothing()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            string missing = Path.Combine(Folder, "no such song.nbl");
            var asked = new List<bool>();

            SongFiles.Picker = saving => { asked.Add(saving); return null; };
            Assert.That(SongFiles.Open().Result, Is.False, "open, cancelled");
            Assert.That(SongFiles.Save().Result, Is.False, "first save, cancelled");
            Assert.That(SongFiles.SaveAs().Result, Is.False, "save as, cancelled");
            Assert.That(asked, Is.EqualTo(new[] { false, true, true }));
            AssertEmptySong(null);
            Assert.That(RecentFiles.List(Host.Registry), Is.Empty);

            SongFiles.Picker = saving => missing;
            Assert.That(SongFiles.Open().Result, Is.False, "a file that isn't there");
            AssertEmptySong(null);
            Assert.That(File.Exists(missing), Is.False, "opening creates nothing");
            Assert.That(RecentFiles.List(Host.Registry), Does.Not.Contain(missing));
        }

        [AvaloniaTest]
        public void SavingForTheFirstTimeAsksOnceAndLaterSavesDoNot()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            string path = NewPath();
            int asked = 0;
            SongFiles.Picker = saving => { asked++; return path; };

            Assert.That(SongFiles.Save().Result, Is.True);
            Assert.That(SongFiles.Save().Result, Is.True);

            Assert.That(asked, Is.EqualTo(1), "the second save has its name");
            Assert.That(File.Exists(path), Is.True);
            AssertEmptySong(path);
            Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { path }));
        }

        [AvaloniaTest]
        public void AnEmptySongSavedIsOpenedAsItWas_OnceAndTwice()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            string path = NewPath();
            SongFiles.Picker = saving => path;
            Assert.That(SongFiles.Save().Result, Is.True);

            Assert.That(SongFiles.Open(path).Result, Is.True);
            AssertEmptySong(path);
            Assert.That(SongFiles.Open(path).Result, Is.True, "again");
            AssertEmptySong(path);
            Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { path }), "listed once");
        }

        [AvaloniaTest]
        public void SaveAsWritesEachFileAndTheRecentListIsNewestFirst()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            string a = NewPath(), b = NewPath(), c = NewPath();

            foreach (var p in new[] { a, b, c })
            {
                SongFiles.Picker = saving => p;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
            }
            AssertEmptySong(c);
            Assert.That(new[] { a, b, c }.All(File.Exists), Is.True);
            Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { c, b, a }));

            // Over a file it wrote before, and then opened again.
            SongFiles.Picker = saving => a;
            Assert.That(SongFiles.SaveAs().Result, Is.True, "overwriting");
            Assert.That(SongFiles.Open(a).Result, Is.True);
            AssertEmptySong(a);
            Assert.That(RecentFiles.List(Host.Registry).First(), Is.EqualTo(a));
        }

        [AvaloniaTest]
        public void CancellingSaveAsKeepsTheSongsFileAndTheList()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            string a = NewPath();
            SongFiles.Picker = saving => a;
            Assert.That(SongFiles.SaveAs().Result, Is.True);

            SongFiles.Picker = saving => null;
            Assert.That(SongFiles.SaveAs().Result, Is.False);

            AssertEmptySong(a);
            Assert.That(RecentFiles.List(Host.Registry), Is.EqualTo(new[] { a }));
        }

        /// <summary>WindowsShellTests.NewMachinesGoWhereThereIsRoom: placement is the shared BuzzGUI.Common code, which the studio's New and machine_add use.</summary>
        [AvaloniaTest]
        public void NewMachinesGoWhereThereIsRoom()
        {
            using var restore = Studio.AfterTest(Reset);
            Fresh();
            var machines = Core.SongCore.Machines;
            var master = machines.Single(m => m.DLL.Info.Type == MachineType.Master);
            var (x, y) = BuzzGUI.Common.MachinePlacement.Free(machines, master.Position.Item1, master.Position.Item2);
            Assert.That(System.Math.Abs(x - master.Position.Item1) >= BuzzGUI.Common.MachinePlacement.MinDx - 1e-4 ||
                        System.Math.Abs(y - master.Position.Item2) >= BuzzGUI.Common.MachinePlacement.MinDy - 1e-4, Is.True, $"({x}, {y}) is off the Master");
            Assert.That(BuzzGUI.Common.MachinePlacement.IsFree(machines, x, y), Is.True);
            var (fx, fy) = BuzzGUI.Common.MachinePlacement.Free(machines, -0.6f, -0.6f);
            Assert.That((fx, fy), Is.EqualTo((-0.6f, -0.6f)), "a free spot stays as asked");
        }

        [AvaloniaTest]
        public void WindowsMidiToolsAreOfferedWhenInstalledOnWindows()
        {
            var was = WindowsMidiTools.Exists;
            try
            {
                WindowsMidiTools.Exists = path => path.EndsWith("midiglass.exe");
                var found = WindowsMidiTools.Available();
                if (System.OperatingSystem.IsWindows()) Assert.That(found.Select(t => t.Label), Is.EqualTo(new[] { "MIDI Glass…" }));
                else Assert.That(found, Is.Empty, "Windows only");
                WindowsMidiTools.Exists = path => false;
                Assert.That(WindowsMidiTools.Available(), Is.Empty, "not installed: no buttons");
            }
            finally { WindowsMidiTools.Exists = was; }
        }

        // ------------------------------------------------------------------ file types (§9 row 16)

        static FileAssociations Assoc(MemoryAssocStore store, string exe = @"C:\Apps\NeoBuzz Live\NeoBuzzLive.exe") => new(store, exe);

        [AvaloniaTest]
        public void MakingTheDefaultRegistersTheSongTypeAndRemovingPutsBackWhatWasThere()
        {
            var store = new MemoryAssocStore();
            store.Set(".nbl", "", "Other.Song");                   // another program owned .nbl
            store.Set(".bmx", "", "NeoBuzzLive.Song");             // a stale entry of an earlier build (CB1 retired .bmx)
            var assoc = Assoc(store);
            Assert.That(assoc.IsDefault, Is.False);
            Assert.That(assoc.CurrentFor(".nbl"), Is.EqualTo("Other.Song"));

            assoc.Make();

            Assert.That(assoc.IsDefault, Is.True);
            Assert.That(store.Get(".nbl", ""), Is.EqualTo("NeoBuzzLive.Song"));
            Assert.That(store.Get(".bmw", ""), Is.Null, ".bmw is not registered any more");
            Assert.That(store.Get(@"NeoBuzzLive.Song\shell\open\command", ""), Is.EqualTo("\"C:\\Apps\\NeoBuzz Live\\NeoBuzzLive.exe\" \"%1\""), "the path may hold spaces");
            Assert.That(store.Get(@"NeoBuzzLive.Song\DefaultIcon", ""), Is.EqualTo("\"C:\\Apps\\NeoBuzz Live\\NeoBuzzLive.exe\",0"));
            Assert.That(store.Get(".nbl", FileAssociations.PreviousValue), Is.EqualTo("Other.Song"));

            assoc.Make();                                          // twice: still the first owner that is kept
            Assert.That(store.Get(".nbl", FileAssociations.PreviousValue), Is.EqualTo("Other.Song"));

            assoc.Remove();

            Assert.That(assoc.IsDefault, Is.False);
            Assert.That(store.Get(".nbl", ""), Is.EqualTo("Other.Song"), "given back");
            Assert.That(store.Get(".bmx", ""), Is.Null, "the stale entry is freed");
            Assert.That(store.Values.Keys.Where(k => k.Key.StartsWith("neobuzzlive.song")), Is.Empty, "the ProgID is gone");
            Assert.That(store.Values.Keys.Where(k => k.Name == FileAssociations.PreviousValue), Is.Empty);
        }

        [AvaloniaTest]
        public void ADefaultOfAnotherInstallIsNotTheOneHere()
        {
            var store = new MemoryAssocStore();
            Assoc(store, @"D:\Old\NeoBuzzLive.exe").Make();
            Assert.That(Assoc(store).IsDefault, Is.False, "the command names another exe");
        }

        [AvaloniaTest]
        public void RemovingLeavesATypeSomeoneElseTookAlone()
        {
            var store = new MemoryAssocStore();
            var assoc = Assoc(store);
            assoc.Make();
            store.Set(".nbl", "", "Other.Song");                   // another program took .nbl since
            assoc.Remove();
            Assert.That(store.Get(".nbl", ""), Is.EqualTo("Other.Song"));
        }

        [AvaloniaTest]
        public void PreferencesOffersTheFileTypesOnWindowsOnly()
        {
            using var restore = Studio.AfterTest(Reset);
            var store = new MemoryAssocStore();

            GeneralPage.Associations = () => null;                 // not Windows
            var none = new GeneralPage(new PrefsModel(Host.Agent.Host.AppControl));
            Assert.That(none.AssociationButton, Is.Null);

            GeneralPage.Associations = () => Assoc(store);
            var page = new GeneralPage(new PrefsModel(Host.Agent.Host.AppControl));
            Assert.That(page.AssociationButton.Content, Is.EqualTo("Make NeoBuzz Live the default for .nbl"));
            Assert.That(page.AssociationText, Is.EqualTo("No program is set for .nbl songs."));

            page.AssociationButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(store.Get(".nbl", ""), Is.EqualTo("NeoBuzzLive.Song"));
            Assert.That(page.AssociationButton.Content, Is.EqualTo("Restore the previous default for .nbl"));

            page.AssociationButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(store.Get(".nbl", ""), Is.Null, "reversible");
            Assert.That(page.AssociationButton.Content, Is.EqualTo("Make NeoBuzz Live the default for .nbl"));
        }
    }
}
