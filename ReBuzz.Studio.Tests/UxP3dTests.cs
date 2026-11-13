// Fork (docs/specs/ux-overhaul.md P3d, decisions 56-61): the machine browser (search, categories from the manifest catalogue, tags and
// use_for, brand, recents and favourites in the settings, the canvas popup by double click or typing, the starters of an empty song)
// and the port dots (drag from an output dot to an input, one undo step, a refused drop says why).

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3dTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static MachineBrowserModel NewModel() => new MachineBrowserModel(Core, Studio.Host.Registry);

        /// <summary>The settings these tests change, put back as found.</summary>
        static void PutBack()
        {
            var r = Studio.Host.Registry;
            r.Write(MachineBrowserModel.RecentsKey, "", MachineBrowserModel.RegistrySection);
            r.Write(MachineBrowserModel.FavouritesKey, "", MachineBrowserModel.RegistrySection);
            MachineBrowserModel.For(Core, r).Reload();
        }

        static IMachine Make(string gear, float x, float y) =>
            MachineEdits.Create(Core, Core.Instruments.First(i => i.MachineDLL.Name == gear && i.Name.Length == 0), x, y);

        // ------------------------------------------------------------------ search and categories

        [AvaloniaTest]
        public void TypicalWordsFindTheRightMachines()
        {
            var model = NewModel();
            string Top(string q, int n = 3) => string.Join(", ", model.Search(q).Take(n).Select(e => e.Name));
            Assert.That(model.Search("drum").Take(3).Select(e => e.Name), Does.Contain("Neo Drums"), "drum -> " + Top("drum"));
            Assert.That(model.Search("reverb").Take(3).Select(e => e.Name), Does.Contain("Neo Verb"), "reverb -> " + Top("reverb"));
            Assert.That(model.Search("compressor").Take(3).Select(e => e.Name), Does.Contain("Neo Comp"), "compressor -> " + Top("compressor"));
            var bass = model.Search("bass").Take(5).ToList();
            Assert.That(bass.Any(e => e.Name == "Neo Poly"), Is.True, "bass -> " + Top("bass", 6));
            Assert.That(bass.All(e => e.Tags.Contains("bass") || e.Name.Contains("bass", StringComparison.OrdinalIgnoreCase)), Is.True, "the top of bass is bass things: " + string.Join("; ", bass.Select(e => e.Name + " " + string.Join("/", e.Tags))));
            Assert.That(model.Search("qqqqzzzz"), Is.Empty);
            Assert.That(model.Search("warm bass").First().Tags, Does.Contain("warm").And.Contain("bass"), "every word counts");
        }

        [AvaloniaTest]
        public void TagsUseForAndBrandAreSearchableAndShown()
        {
            var model = NewModel();
            var poly = model.Find("Neo Poly")!;
            Assert.That(poly.Category, Is.EqualTo("Synths"));
            Assert.That(poly.Tags[0], Is.EqualTo("Neo"), "the brand is the first tag");
            Assert.That(poly.Tags, Does.Contain("subtractive"));
            Assert.That(poly.UseFor, Does.Contain("polysynth"));
            Assert.That(model.Search("subtractive").Select(e => e.Name), Does.Contain("Neo Poly"), "a tag");
            Assert.That(model.Search("polysynth").Select(e => e.Name), Does.Contain("Neo Poly"), "a word of use_for");
            Assert.That(model.Search("neo").Count, Is.GreaterThan(10), "the brand");
            Assert.That(model.Search("polysynth").First().Name, Is.EqualTo("Neo Poly"));
            var row = BrowserRows.Item(new MachineBrowserModel.Row("gear:Neo Poly", "Neo Poly", poly, false), model, detail: true);
            var text = string.Join(" | ", row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("Neo").And.Contain("subtractive").And.Contain("polysynth"), "a result shows the tags and use_for: " + text);
        }

        [AvaloniaTest]
        public void CategoriesFilterAndTheListGroupsByThem()
        {
            var model = NewModel();
            Assert.That(MachineBrowserModel.Categories, Is.EqualTo(new[] { "Synths", "Drums", "Samplers", "Dynamics", "EQ & Filter", "Delay & Reverb", "Modulation", "Distortion", "Utility", "Control" }),
                "the ten categories of the manifest catalogue (agent-knowledge.md K0)");
            var drums = model.Search("", "Drums");
            Assert.That(drums.Select(e => e.Name), Does.Contain("Neo Drums"));
            Assert.That(drums.All(e => e.Category == "Drums"), Is.True);
            Assert.That(model.Search("bass", "Dynamics").All(e => e.Category == "Dynamics"), Is.True, "a filter narrows a search too");
            var headers = model.Rows("").Where(r => r.Header).Select(r => r.Text).ToList();
            var order = MachineBrowserModel.Categories.Where(headers.Contains).ToList();
            Assert.That(headers.Where(MachineBrowserModel.Categories.Contains), Is.EqualTo(order), "in the catalogue's order");
            Assert.That(model.Rows("", "Synths").Where(r => r.Header).Select(r => r.Text), Is.EqualTo(new[] { "Synths" }));
            Assert.That(model.Rows("", "Synths").Where(r => !r.Header).All(r => r.Entry!.Category == "Synths"), Is.True);
        }

        // ------------------------------------------------------------------ recents and favourites

        [AvaloniaTest]
        public void RecentsAndFavouritesAreKeptInTheSettings()
        {
            using var restore = Studio.AfterTest(PutBack);
            PutBack();
            var model = NewModel();
            var poly = model.Find("Neo Poly")!;
            var eq = model.Find("Neo EQ")!;
            var verb = model.Find("Neo Verb")!;
            model.MarkUsed(poly);
            model.MarkUsed(eq);
            model.MarkUsed(poly);
            Assert.That(model.Recents, Is.EqualTo(new[] { "Neo Poly", "Neo EQ" }), "most recent first, no repeats");
            model.ToggleFavourite(verb);
            Assert.That(model.IsFavourite(verb), Is.True);

            var again = NewModel();           // another window, or the next run: it reads the settings
            Assert.That(again.Recents, Is.EqualTo(new[] { "Neo Poly", "Neo EQ" }));
            Assert.That(again.Favourites, Is.EqualTo(new[] { "Neo Verb" }));
            var rows = again.Rows("").Select(r => r.Tag).ToList();
            Assert.That(rows, Does.Contain("#Recent").And.Contain("#Favourites"));
            Assert.That(rows.IndexOf("#Recent"), Is.LessThan(rows.IndexOf("#Synths")), "above the categories");
            Assert.That(again.Search("", favouritesOnly: true).Select(e => e.Name), Is.EqualTo(new[] { "Neo Verb" }));
            again.ToggleFavourite(again.Find("Neo Verb")!);
            Assert.That(NewModel().Favourites, Is.Empty, "unstarring is kept too");
            for (int i = 0; i < 12; i++) again.MarkUsed(again.Entries[i]);
            Assert.That(NewModel().Recents.Count, Is.EqualTo(MachineBrowserModel.MaxRecents), "a short list");
        }

        [AvaloniaTest]
        public void ThePanelAndThePopupShareOneModel()
        {
            var (window, view) = OpenEmpty();
            try
            {
                Assert.That(window.Browser.Model, Is.SameAs(view.Model), "one model, so one set of recents and favourites");
                window.Browser.Open();
                Settle();
                window.Browser.Filter.Category = "Drums";
                Settle();
                Assert.That(window.Browser.Rows.Where(r => r.StartsWith("gear:")), Does.Contain("gear:Neo Drums"));
                Assert.That(window.Browser.Rows.Where(r => r.StartsWith("#")), Is.EqualTo(new[] { "#Drums" }));
            }
            finally
            {
                window.Browser.Filter.Category = null;
                window.Browser.Close();
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the canvas popup

        [AvaloniaTest]
        public void ADoubleClickOnTheCanvasOpensTheBrowserAndAddsWhereYouClickedInOneUndoStep()
        {
            using var restore = Studio.AfterTest(PutBack);
            var (window, view) = OpenEmpty();
            try
            {
                var at = view.PointOf(-0.6, -0.5, window);
                DoubleClick(window, at);
                Assert.That(view.Popup.IsOpen, Is.True, "a double click on empty canvas");
                view.Popup.Search.Text = "drum";
                Settle();
                Assert.That(view.Popup.List.SelectedItem, Is.Not.Null, "the best match is ready");
                Assert.That(view.Popup.Rows.First(r => r.StartsWith("gear:")), Is.EqualTo(((ListBoxItem)view.Popup.List.SelectedItem!).Tag), "the first row");
                view.Popup.Search.Text = "neo drums";
                Settle();
                Assert.That(((ListBoxItem)view.Popup.List.SelectedItem!).Tag, Is.EqualTo("gear:Neo Drums"), string.Join(", ", view.Popup.Rows));
                int before = Core.SongCore.Machines.Count;
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle();
                Assert.That(view.Popup.IsOpen, Is.False);
                var drums = MachineOf("Neo Drums")!;
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(before + 1));
                Assert.That(Math.Abs(view.PointOf(drums, window).X - at.X), Is.LessThan(2), "where it was clicked (x)");
                Assert.That(Math.Abs(view.PointOf(drums, window).Y - at.Y), Is.LessThan(2), "where it was clicked (y)");
                Assert.That(view.Canvas.Selection, Is.EqualTo(new[] { drums }), "and selected");
                Assert.That(Model().Recents[0], Is.EqualTo("Neo Drums"), "a recent now");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(MachineOf("Neo Drums"), Is.Null, "one undo takes it away");
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(before));
            }
            finally
            {
                Close(window);
            }
        }

        static MachineBrowserModel Model() => MachineBrowserModel.For(Core, Studio.Host.Registry);

        [AvaloniaTest]
        public void TypingOnTheCanvasSeedsTheSearch()
        {
            using var restore = Studio.AfterTest(PutBack);
            var (window, view) = OpenEmpty();
            try
            {
                Click(window, view.PointOf(0.5, 0.5, window));       // empty canvas: nothing selected, the canvas has the keys
                Assert.That(view.Canvas.TypesToSearch, Is.True);
                Assert.That(view.Popup.IsOpen, Is.False);
                window.KeyTextInput("rev");
                Settle();
                Assert.That(view.Popup.IsOpen, Is.True, "typing opens it");
                Assert.That(view.Popup.Search.Text, Is.EqualTo("rev"), "the typed text is the search");
                Assert.That(view.Popup.Rows, Does.Contain("gear:Neo Verb"));
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle();
                Assert.That(view.Popup.IsOpen, Is.False, "Esc closes it");
                Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(1), "and adds nothing");

                // With a machine selected the letters are M, S and B, not typing.
                var poly = Make("Neo Poly", -0.5f, -0.5f);
                view.Canvas.Select(new[] { poly });
                Assert.That(view.Canvas.TypesToSearch, Is.False);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheStartersOfAnEmptySongAddInOneClickAndOneUndoStep()
        {
            using var restore = Studio.AfterTest(PutBack);
            var (window, view) = OpenEmpty();
            try
            {
                Assert.That(view.Starters.IsVisible, Is.True, "a song with only its Master offers the starters");
                var buttons = view.Starters.GetLogicalDescendants().OfType<Button>().Select(b => (string)b.Content!).ToList();
                Assert.That(buttons, Is.EqualTo(new[] { "Neo Poly", "Neo Drums", "Neo Wave" }));
                window.Browser.Open();
                Settle();
                Assert.That(window.Browser.Rows, Does.Contain("#Start here").And.Contain("gear:Neo Poly"), "the browser's empty state too");
                window.Browser.Close();

                var button = view.Starters.GetLogicalDescendants().OfType<Button>().First(b => (string)b.Content! == "Neo Poly");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle();
                var poly = MachineOf("Neo Poly")!;
                Assert.That(poly, Is.Not.Null);
                Assert.That(Graph(), Does.Contain("→Master"), "an instrument is wired to the Master: it makes sound");
                Assert.That(view.Starters.IsVisible, Is.False, "the empty state goes");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Graph(), Is.EqualTo("Master"), "one undo step");
                Assert.That(view.Starters.IsVisible, Is.True, "and it is back");
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ port dots

        [AvaloniaTest]
        public void ADragFromAnOutputDotToAnInputConnectsInOneUndoStep()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var poly = Make("Neo Poly", -0.7f, -0.5f);
                var eq = Make("Neo EQ", 0.5f, -0.5f);
                Settle();
                string before = Graph();
                var from = view.FromCanvas(view.Canvas.OutPortOf(poly), window);
                var to = view.FromCanvas(view.Canvas.InPortOf(eq), window);
                window.MouseMove(from);
                Settle();
                Assert.That(view.Canvas.PortHover, Is.SameAs(poly), "hovering the dot");
                Assert.That(view.Canvas.HoverTip, Does.Contain("Output of Poly").And.Contain("drag"), "hover shows the port");
                Drag(window, from, to);
                Assert.That(Wire("Poly", "EQ"), Is.Not.Null, "connected: " + Graph());
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Wire("Poly", "EQ"), Is.Null, "one undo step");
                Assert.That(Graph(), Is.EqualTo(before));

                // Shift-drag from the box stays as it was.
                Drag(window, view.PointOf(poly, window), view.PointOf(eq, window), mods: RawInputModifiers.Shift);
                Assert.That(Wire("Poly", "EQ"), Is.Not.Null, "Shift-drag still connects");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AnInvalidDropIsRefusedVisiblyAndChangesNothing()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var poly = Make("Neo Poly", -0.7f, -0.5f);
                var drums = Make("Neo Drums", 0.5f, -0.5f);
                Settle();
                string before = Graph();
                var from = view.FromCanvas(view.Canvas.OutPortOf(poly), window);
                Drag(window, from, view.PointOf(drums, window));
                Assert.That(Graph(), Is.EqualTo(before), "an instrument has no input: nothing connected");
                Assert.That(view.Canvas.Refusal, Does.Contain("Drums").And.Contain("no input"), "and it says why: " + view.Canvas.Refusal);
                Assert.That(MachineEdits.WhyNotConnect(Core, poly, poly), Does.Contain("itself"));
                Assert.That(MachineEdits.WhyNotConnect(Core, poly, MachineOf("Master")), Does.Contain("Already"), "the wire to Master exists");
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ contrast

        [AvaloniaTest]
        public void TheBrowsersTextAndTheRefusalReadAtFourAndAHalfToOne()
        {
            double Of(Avalonia.Media.Color a, Avalonia.Media.Color b) => StudioTokens.Contrast(a, b);
            Assert.That(Of(StudioTokens.TextPrimary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "names");
            Assert.That(Of(StudioTokens.TextSecondary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "tags, use_for, headers");
            Assert.That(Of(StudioTokens.Accent, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "a lit star");
            Assert.That(Of(StudioTokens.ErrorText, StudioTokens.SevereBack), Is.GreaterThanOrEqualTo(4.5), "the refusal's words");
            Assert.That(Of(StudioTokens.Accent, StudioTokens.SurfaceBase), Is.GreaterThanOrEqualTo(3), "a port dot on the canvas");
        }
    }
}
