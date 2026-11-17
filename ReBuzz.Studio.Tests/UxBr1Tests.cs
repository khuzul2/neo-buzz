// Fork (docs/specs/browser.md, BR1): the browser in the left bar: the sources and the results, one search over everything, the collapse to
// one column, places and the index, the preview strip and its mix, the drag format, and hot-swap in the Machine View (a drop on a box,
// a drop on empty canvas, the context menu's swap mode), all with one undo step.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Browser;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxBr1Tests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static MachineBrowserModel Model => MachineBrowserModel.For(Core, Studio.Host.Registry);

        /// <summary>The settings these tests change, put back as found.</summary>
        static void PutBack()
        {
            var r = Studio.Host.Registry;
            r.Write(BrowserPanel.OpenKey, false, BrowserPanel.RegistrySection);
            r.Write(BrowserPanel.WidthKey, BrowserPanel.DefaultWidth, BrowserPanel.RegistrySection);
            r.Write(BrowserPanel.SourceKey, BrowserSource.All, BrowserPanel.RegistrySection);
            r.Write(BrowserPanel.TreeKey, true, BrowserPanel.RegistrySection);
            r.Write(BrowserLibrary.PlacesKey, "", BrowserPanel.RegistrySection);
            r.Write(BrowserLibrary.RecentFilesKey, "", BrowserPanel.RegistrySection);
            r.Write(BrowserCollections.SettingsKey, "", BrowserPanel.RegistrySection);
            Model.Library.Reload();
            Model.Collections.Reload();
            Model.EndSwap();
        }

        static string Temp(string name) => ReBuzzTests.Automation.TestTemp.Path("br1-studio-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

        /// <summary>A WAV of a sine (16-bit mono) so a preview has something to mix.</summary>
        static void WriteSine(string path, double seconds, int rate = 44100, double hz = 440)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int frames = (int)(seconds * rate);
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * 2);
            for (int i = 0; i < frames; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 20000));
        }

        static Point ViewPoint(MachineView view, IMachine m) => view.PointOf(m, view.Canvas);

        static IMachine MakeEffect(string gear, string name, float x, float y)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, x, y);
            return Machine(name);
        }

        // ------------------------------------------------------------------ layout

        [AvaloniaTest]
        public void TheSourcesAreCollectionsCategoriesAndPlaces()
        {
            PutBack();
            var sources = Model.Sources();
            Assert.That(sources[0].Id, Is.EqualTo(BrowserSource.All));
            Assert.That(sources.Where(s => s.Section == "Collections").Select(s => s.Title), Is.EqualTo(new[] { "Favourites", "Red", "Orange", "Yellow", "Green", "Blue", "Violet", "Grey" }), "favourites and seven colour tags");
            Assert.That(sources.Where(s => s.Section == "Categories").Select(s => s.Title),
                Is.EqualTo(new[] { "Instruments", "Drums", "Audio Effects", "MIDI and Control", "Plug-ins", "Presets", "Clips", "Samples", "Neo Audio" }));
            Assert.That(sources.Where(s => s.Section == "Places").Select(s => s.Title), Is.EqualTo(new[] { "Gear samples", "This song's audio", "Recent" }), "no added folder yet");
            Assert.That(Model.ItemsOf("cat:drums").Select(i => i.Name), Does.Contain("Neo Drums"));
            Assert.That(Model.ItemsOf("cat:instruments").Select(i => i.Name), Does.Contain("Neo Poly").And.Not.Contain("Neo Drums"));
            Assert.That(Model.ItemsOf("cat:effects").Select(i => i.Name), Does.Contain("Neo Verb").And.Not.Contain("Neo Poly"));
            Assert.That(Model.ItemsOf("cat:control").Select(i => i.Name), Is.Not.Empty);
        }

        [AvaloniaTest]
        public void UnderAboutThreeHundredEightyPointsTheSourcesBecomeADropdown()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var panel = window.Browser;
                panel.Open();
                panel.PanelWidth = 440;
                Settle();
                Assert.That(panel.IsOneColumn, Is.False);
                Assert.That(panel.SourcesList.IsEffectivelyVisible, Is.True, "two columns: the sources beside the results");
                Assert.That(panel.SourcesBox.IsVisible, Is.False);
                Studio.Screenshot(window, "browser-two-columns");
                var kind = ReBuzz.Studio.Shell.Design.StudioTheme.Kind;
                try
                {
                    ReBuzz.Studio.Shell.Design.StudioTheme.Kind = ReBuzz.Studio.Shell.Design.AppearanceTheme.Light;
                    Settle();
                    Studio.Screenshot(window, "browser-two-columns-light");
                }
                finally { ReBuzz.Studio.Shell.Design.StudioTheme.Kind = kind; }
                Settle();
                panel.PanelWidth = 300;
                Settle();
                Assert.That(panel.IsOneColumn, Is.True, "one column");
                Assert.That(panel.SourcesList.IsEffectivelyVisible, Is.False);
                Assert.That(panel.SourcesBox.IsEffectivelyVisible, Is.True);
                Studio.Screenshot(window, "browser-one-column");
                panel.SourcesBox.SelectedIndex = panel.Model.Sources().FindIndex(s => s.Id == "cat:drums");
                Settle();
                Assert.That(panel.CurrentSource, Is.EqualTo("cat:drums"), "the dropdown picks the source");
                Assert.That(panel.Rows, Does.Contain("gear:Neo Drums"));
                Assert.That(panel.Filter.IsVisible, Is.False, "the machine filter is for All");
                panel.PanelWidth = 400;
                Settle();
                Assert.That(panel.IsOneColumn, Is.False, "wide enough again");
                Assert.That(panel.SourcesList.SelectedItem, Is.Not.Null.And.Property("Tag").EqualTo("src:cat:drums"), "the choice carries over");
            }
            finally
            {
                PutBack();
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ResultsAreATreeOrAListAndOneSearchCoversEveryKind()
        {
            var (window, view) = OpenEmpty();
            string samples = Temp("samples");
            WriteSine(Path.Combine(samples, "Drums", "Kick 808.wav"), 0.5);
            WriteSine(Path.Combine(samples, "Drums", "Snare.wav"), 0.3);
            try
            {
                var panel = window.Browser;
                panel.Open();
                panel.PanelWidth = 440;
                Assert.That(panel.AddPlaceFolder(samples), Is.True);
                Assert.That(panel.Model.Library.WaitForScan(), Is.True);
                Settle();
                Assert.That(panel.Model.Sources().Select(s => s.Title), Does.Contain(Path.GetFileName(samples)), "the folder is a place");
                panel.SelectSource("place:" + samples);
                Settle();
                string kick = "audio:" + Path.Combine(samples, "Drums", "Kick 808.wav");
                Assert.That(panel.Rows, Does.Contain(kick));
                Assert.That(panel.Rows.Any(r => r.StartsWith("#") && r.Contains("Drums")), Is.True, "a tree: the folder is a header");
                panel.TreeToggle.IsChecked = false;
                Settle();
                Assert.That(panel.Rows.Any(r => r.StartsWith("#")), Is.False, "a list: no headers");
                Assert.That(panel.Rows, Does.Contain(kick));
                panel.TreeToggle.IsChecked = true;
                // One search over everything: machines, presets, patterns of the selection and files, whatever source is chosen.
                panel.Search.Text = "kick";
                Settle();
                Assert.That(panel.Rows, Does.Contain(kick), "the file is found from the search box");
                panel.Search.Text = "poly";
                Settle();
                Assert.That(panel.Rows, Does.Contain("gear:Neo Poly"), "and a machine");
                panel.Search.Text = "";
                panel.SelectSource(BrowserSource.All);
                Settle();
                Assert.That(panel.Rows, Does.Contain("gear:Neo Poly"));
                // The index status and its file.
                Assert.That(File.Exists(panel.Model.Library.IndexPath), Is.True);
            }
            finally
            {
                PutBack();
                Close(window);
            }
        }

        [AvaloniaTest]
        public void PresetsAreItemsAndFavouritesAndColourTagsAreCollections()
        {
            PutBack();
            var presets = Model.PresetItems().ToList();
            Assert.That(presets, Is.Not.Empty, "the factory presets beside the machines in Gear");
            var one = presets.First(p => p.Id == "Pedal Juno106");
            Assert.That(one.Kind, Is.EqualTo(BrowserKind.Preset));
            Assert.That(one.Drag, Is.EqualTo(new BrowserDragData(BrowserKind.Preset, "Pedal Juno106", one.Preset, one.Name)));
            Assert.That(Model.ItemsOf("cat:presets").Select(i => i.Tag), Does.Contain(one.Tag));

            Model.ToggleFavourite(one);
            Assert.That(Model.ItemsOf(BrowserSource.Favourites).Select(i => i.Tag), Does.Contain(one.Tag));
            Model.SetColour(one, 5);
            Assert.That(Model.ItemsOf("col:5").Select(i => i.Tag), Is.EqualTo(new[] { one.Tag }), "a tag is a collection");
            var poly = MachineBrowserModel.MachineItem(Model.Find("Neo Poly")!);
            Model.SetColour(poly, 2);
            Assert.That(Model.ItemsOf("col:2").Select(i => i.Name), Is.EqualTo(new[] { "Neo Poly" }));
            Assert.That(Model.IsFavourite(Model.Find("Neo Poly")!), Is.True, "a tag makes a machine a favourite too (the star)");
            // Kept in the settings: a fresh read.
            Model.Collections.Reload();
            Assert.That(Model.ItemsOf("col:5").Count, Is.EqualTo(1));
            PutBack();
            Assert.That(Model.ItemsOf("col:5"), Is.Empty);
        }

        // ------------------------------------------------------------------ drag

        [AvaloniaTest]
        public void EveryKindOfRowCarriesDragDataInOneFormat()
        {
            PutBack();
            var kinds = new (string Source, BrowserKind Kind)[] { ("cat:instruments", BrowserKind.Machine), ("cat:presets", BrowserKind.Preset) };
            foreach (var (source, kind) in kinds)
            {
                var item = Model.ItemsOf(source).First();
                Assert.That(item.Kind, Is.EqualTo(kind));
                var packed = BrowserDragSource.Package(item.Drag);
                Assert.That(BrowserDragSource.Read(packed), Is.EqualTo(item.Drag), source + ": what is dragged is what a drop target reads");
            }
            var audio = new BrowserDragData(BrowserKind.Audio, "/Users/x/Kick.wav");
            Assert.That(BrowserDragSource.Read(BrowserDragSource.Package(audio)), Is.EqualTo(audio));
            Assert.That(BrowserDragSource.Read(new Avalonia.Input.DataTransfer()), Is.Null, "a drop of anything else is not a browser item");
            // The rows in the panel are draggable: the machine row, a preset row and a file row all hand over their data.
            var (window, view) = OpenEmpty();
            string samples = Temp("drag");
            WriteSine(Path.Combine(samples, "Hat.wav"), 0.2);
            try
            {
                var panel = window.Browser;
                panel.Open();
                panel.AddPlaceFolder(samples);
                panel.Model.Library.WaitForScan();
                panel.SelectSource("place:" + samples);
                Settle();
                panel.List.SelectedItem = panel.List.Items.OfType<ListBoxItem>().First(i => (i.Tag as string)!.StartsWith("audio:"));
                Settle();
                Assert.That(panel.SelectedItem()!.Drag, Is.EqualTo(new BrowserDragData(BrowserKind.Audio, Path.Combine(samples, "Hat.wav"), null, "Hat")));
                panel.SelectSource("cat:presets");
                Settle();
                panel.List.SelectedItem = panel.List.Items.OfType<ListBoxItem>().First(i => (i.Tag as string)!.StartsWith("preset:"));
                Assert.That(panel.SelectedItem()!.Drag.Kind, Is.EqualTo(BrowserKind.Preset));
            }
            finally
            {
                PutBack();
                Close(window);
            }
        }

        // ------------------------------------------------------------------ preview

        [AvaloniaTest]
        public void ASelectedSampleListensRawAndMixesIntoTheMaster()
        {
            var (window, view) = OpenEmpty();
            string samples = Temp("preview");
            string file = Path.Combine(samples, "Tone.wav");
            WriteSine(file, 1.0);
            try
            {
                var panel = window.Browser;
                panel.Open();
                panel.AddPlaceFolder(samples);
                panel.Model.Library.WaitForScan();
                panel.SelectSource("place:" + samples);
                Settle();
                panel.List.SelectedItem = panel.List.Items.OfType<ListBoxItem>().First(i => (i.Tag as string)!.StartsWith("audio:"));
                Settle();
                var strip = panel.Strip;
                Assert.That(strip.Engine.Loaded, Is.True, "the file is loaded for the strip");
                Assert.That(strip.Engine.Seconds, Is.EqualTo(1.0).Within(0.01));
                Assert.That(strip.Text, Does.Contain("1.0").And.Contain("44.1 kHz").And.Contain("mono"), "length and rate: " + strip.Text);
                Assert.That(strip.Wave.Peaks.Length, Is.GreaterThan(0), "the waveform");
                Assert.That(strip.Wave.Peaks.Max(), Is.GreaterThan(0.5f));
                Assert.That(strip.Engine.IsPlaying, Is.False, "the speaker is off: it only shows");
                strip.Listening = true;
                Settle();
                Assert.That(strip.Engine.IsPlaying, Is.True, "the speaker on: it plays");
                Assert.That(strip.PlayedInSync, Is.False, "the transport stands: raw");
                var buffer = new float[512];
                BrowserPreview.MixInto(Core, buffer, 0, 256);
                Assert.That(buffer.Take(512).Max(Math.Abs), Is.GreaterThan(1000f), "it is in the master's block (Buzz's scale)");
                strip.Sync = true;
                Settle();
                Assert.That(strip.PlayedInSync, Is.False, "Sync with the transport stopped plays raw");
                // It stops at the end of a raw play, and the speaker off stops it at once.
                strip.Listening = false;
                Assert.That(strip.Engine.IsPlaying, Is.False);
                var silent = new float[512];
                BrowserPreview.MixInto(Core, silent, 0, 256);
                Assert.That(silent.All(x => x == 0), Is.True, "nothing is added when it is off");
            }
            finally
            {
                panel_Stop(window);
                PutBack();
                Close(window);
            }
        }

        static void panel_Stop(MainWindow w) => w.Browser.Strip.Stop();

        // ------------------------------------------------------------------ hot-swap

        [AvaloniaTest]
        public void DroppingAMachineOnABoxReplacesItKeepingConnectionsAndPatternsInOneUndoStep()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var trim = MakeEffect("Neo Utility", "Trim", 0.1f, -0.2f);
                Core.SongCore.ConnectMachines(trim, Machine("Master"), new[] { 0 }, new[] { 0 }, 0x2000, 0x4000);
                trim.CreatePattern("P1", 16);
                Settle();
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"));
                var made = view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Machine, "Neo Comp"), ViewPoint(view, trim));
                Settle();
                Assert.That(made, Is.Not.Null);
                var now = (MachineCore)Machine("Trim");
                Assert.That(now.DLL.Name, Is.EqualTo("Neo Comp"), "the box now holds the dropped machine");
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"), "connections kept");
                Assert.That(Wire("Trim", "Master").Amp, Is.EqualTo(0x2000), "with their gain");
                Assert.That(now.Patterns.Select(p => p.Name), Does.Contain("P1"), "patterns kept where they fit");
                Assert.That(view.Canvas.Selection, Does.Contain(made), "and selected");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Utility"), "one undo step brings the first back");
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"));
                Core.SongCore.ActionStack.Redo();
                Settle();
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Comp"), "and redo");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AGeneratorCannotTakeAnEffectsPlaceAndTheMasterIsNeverReplaced()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var trim = MakeEffect("Neo Utility", "Trim", 0.1f, -0.2f);
                Settle();
                string before = Graph();
                Assert.That(view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Machine, "Neo Poly"), ViewPoint(view, trim)), Is.Null);
                Assert.That(view.Canvas.Refusal, Does.Contain("effect"), "it says why: " + view.Canvas.Refusal);
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Utility"));
                Assert.That(view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Machine, "Neo Comp"), ViewPoint(view, Machine("Master"))), Is.Null);
                Assert.That(view.Canvas.Refusal, Does.Contain("Master"));
                Assert.That(view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Audio, "/x/kick.wav"), ViewPoint(view, trim)), Is.Null);
                Assert.That(view.Canvas.Refusal, Does.Contain("No file"), "audio is the drop router's now (DD1): a file that isn't there is refused");
                Assert.That(Graph(), Is.EqualTo(before), "nothing changed");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void APresetOnAMachineOfItsOwnTypeLoadsItAndOnEmptyCanvasAddsTheMachine()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var preset = Model.PresetItems().First(p => p.Id == "Pedal Juno106");
                // On empty canvas: the machine, with the preset, at that place; one undo removes both.
                var empty = new Point(60, 60);
                Assert.That(view.Canvas.MachineAt(view.Canvas.ToLogical(empty)), Is.Null);
                var made = view.Canvas.DropBrowserItem(preset.Drag, empty);
                Settle();
                Assert.That(made, Is.Not.Null);
                Assert.That(made.DLL.Name, Is.EqualTo("Pedal Juno106"));
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(2));
                var values = made.AllNonInputParameters().Select(p => p.GetValue(0)).ToList();
                var plain = Model.PresetItems().Where(p => p.Id == "Pedal Juno106").Select(p => p.Preset).Distinct().Skip(1).First();
                // The same machine's box takes another preset: it loads instead of replacing.
                string name = made.Name;
                int machines = Core.SongCore.Machines.Count;
                var again = view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Preset, "Pedal Juno106", plain), ViewPoint(view, made));
                Settle();
                Assert.That(again, Is.SameAs(made), "the same machine: nothing was replaced");
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines));
                var after = made.AllNonInputParameters().Select(p => p.GetValue(0)).ToList();
                Assert.That(after, Is.Not.EqualTo(values), "the preset's values are in");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(made.AllNonInputParameters().Select(p => p.GetValue(0)).ToList(), Is.EqualTo(values), "one undo step puts the values back");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(1), "and one more removes what the drop on the canvas made");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheMachineMenuHotSwapPutsTheBrowserInSwapModeAndADoubleClickReplaces()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var trim = MakeEffect("Neo Utility", "Trim", 0.1f, -0.2f);
                Core.SongCore.ConnectMachines(trim, Machine("Master"), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Settle();
                Click(window, view.PointOf(trim, window), MouseButton.Right);
                var menu = view.Canvas.OpenMenu;
                var item = menu!.Items.OfType<MenuItem>().FirstOrDefault(x => x.Header as string == "Hot-swap");
                Assert.That(item, Is.Not.Null, "the machine's menu has Hot-swap: " + string.Join(", ", menu.Items.OfType<MenuItem>().Select(x => x.Header)));
                ClickItem(item!);
                Settle();
                var panel = window.Browser;
                Assert.That(panel.IsOpen, Is.True, "the browser opens");
                Assert.That(panel.Model.SwapTarget, Is.SameAs(trim));
                Assert.That(panel.SwapBanner.IsVisible, Is.True, "and says so, highlighted");
                Studio.Screenshot(window, "browser-swap-mode");
                panel.Search.Text = "neo comp";
                Settle();
                panel.List.SelectedItem = panel.List.Items.OfType<ListBoxItem>().First(i => i.Tag as string == "gear:Neo Comp");
                panel.Activate();
                Settle();
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Comp"), "the double-click replaced it");
                Assert.That(Graph(), Is.EqualTo("Master; Trim; Trim→Master"));
                Assert.That(panel.Model.SwapTarget, Is.Null, "swap mode ends");
                Assert.That(panel.SwapBanner.IsVisible, Is.False);
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Utility"), "one undo step");
                // A refused swap keeps the mode and says why; Escape cancels.
                panel.Model.BeginSwap(Machine("Trim"));
                panel.Search.Text = "neo poly";
                Settle();
                panel.List.SelectedItem = panel.List.Items.OfType<ListBoxItem>().First(i => i.Tag as string == "gear:Neo Poly");
                panel.Activate();
                Assert.That(((MachineCore)Machine("Trim")).DLL.Name, Is.EqualTo("Neo Utility"));
                Assert.That(panel.Model.SwapTarget, Is.Not.Null, "still waiting");
                Assert.That(panel.SwapSaid, Does.Contain("effect"));
                panel.Model.EndSwap();
                panel.Search.Text = "";
            }
            finally
            {
                PutBack();
                Close(window);
            }
        }

        [AvaloniaTest]
        public void APlainDropOfAGeneratorOnEmptyCanvasAddsItThere()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var empty = new Point(80, 90);
                var made = view.Canvas.DropBrowserItem(new BrowserDragData(BrowserKind.Machine, "Neo Poly"), empty);
                Settle();
                Assert.That(made, Is.Not.Null);
                Assert.That(made.DLL.Name, Is.EqualTo("Neo Poly"));
                var at = CanvasMap.Clamp(view.Canvas.Map.PositionAt(view.Canvas.ToLogical(empty)));
                Assert.That(made.Position.Item1, Is.EqualTo((float)at.X).Within(0.35), "where it was dropped (it is moved aside when something is there)");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Graph(), Is.EqualTo("Master"), "one undo step");
            }
            finally
            {
                Close(window);
            }
        }
    }
}
