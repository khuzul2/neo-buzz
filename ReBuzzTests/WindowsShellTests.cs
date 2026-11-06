using BuzzGUI.Interfaces;
using ReBuzz.Common;
using ReBuzzTests.Automation;
using System;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork: small fixes of the Windows app's shell from the UI gallery's review (docs/specs/ui-gallery.md):
    /// the Master's menu (win-203), Preferences' MIDI pages (win-401, win-403), the wavetable's file
    /// browser (win-050).
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class WindowsShellTests
    {
        [Test]
        public void TheMastersMenuHasNoCut()
        {
            // The Master can't be deleted: Cut only copied it, under a name that says it goes away.
            var items = BuzzGUI.MachineView.MachineControl.MasterMenu(null, null, null).Where(i => !i.IsSeparator).Select(i => i.Text).ToList();
            Assert.That(items, Does.Not.Contain("Cut"));
            Assert.That(items, Is.EqualTo(new[] { "Parameters...", "Patterns", "Signal Analysis...", "Copy", "Paste" }));
        }

        [Test]
        public void PreferencesMidiPagesSayControllersAndRemoveNeedsASelection()
        {
            using var driver = new Driver();
            driver.Start();
            var window = new PreferencesWindow(driver.Core, new FakeInMemoryRegistry());
            try
            {
                var texts = LogicalTreeTexts(window).ToList();
                Assert.That(texts, Does.Not.Contain("Contollers:"), "the typo");
                Assert.That(texts.Count(t => t == "Controllers:"), Is.EqualTo(2), "MIDI Input and MIDI Bindings");
                Assert.That(window.btOk.Content, Is.EqualTo("OK"));

                // Remove and Modify work on a selection: off without one, on with one.
                Assert.That(window.lvControllers.SelectedIndex, Is.EqualTo(-1));
                Assert.That((window.btRemoveController.IsEnabled, window.btModifyController.IsEnabled), Is.EqualTo((false, false)), "nothing to remove");
                Assert.That((window.btRemoveDAWController.IsEnabled, window.btModifyDAWController.IsEnabled), Is.EqualTo((false, false)));
                window.lvControllers.Items.Add(new PreferencesWindow.ControllerVM { Name = "Knob", Channel = 1, Controller = "7" });
                window.lvControllers.SelectedIndex = window.lvControllers.Items.Count - 1;
                Assert.That((window.btRemoveController.IsEnabled, window.btModifyController.IsEnabled), Is.EqualTo((true, true)));
                Assert.That(window.btRemoveDAWController.IsEnabled, Is.False, "each list has its own selection");
                window.btRemoveController.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.That(window.lvControllers.Items.OfType<PreferencesWindow.ControllerVM>().Any(c => c.Name == "Knob"), Is.False);
                Assert.That(window.btRemoveController.IsEnabled, Is.False, "removed: nothing selected again");
            }
            finally
            {
                window.Close();
            }
        }

        static System.Collections.Generic.IEnumerable<string> LogicalTreeTexts(DependencyObject root)
        {
            if (root is TextBlock t && t.Text != null) yield return t.Text;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var text in LogicalTreeTexts(child))
                    yield return text;
        }

        // ------------------------------------------------------------------ F10: Song Notes (win-060)

        [Test]
        public void F10ShowsTheNotesWrittenElsewhereAndTheOldInfoTextImported()
        {
            // The F10 view showed Buzz's old info text only: notes the Mac app or an agent wrote looked lost.
            using var rig = new AgentServerTests.Rig();
            var core = rig.Driver.Core;
            const string notes = "# Ideas\n\n- **louder** chorus\n- [reference](https://example.com)";
            var (error, text, _) = rig.Call("song_notes_write", new JsonObject { ["text"] = notes });
            Assert.That(error, Is.False, text);

            var view = new ReBuzz.AppViews.SongNotesView(core);
            Assert.That(view.Editor.Text, Is.EqualTo(notes), "the editor has the song's notes");
            Assert.That(view.Page.Blocks.Count, Is.EqualTo(3), "a heading and two items, as they read");
            Assert.That(view.Links().Single().ToolTip, Is.EqualTo("https://example.com"));
            Assert.That(view.Banner.Visibility, Is.EqualTo(Visibility.Collapsed));

            // A format on the selection is one step on the song's history; Undo takes it back.
            view.Editor.Select(2, 5);                                   // "Ideas"
            view.ToolButton("B").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.That(core.SongNotes.Text, Does.StartWith("# **Ideas**"));
            Assert.That(view.Editor.Text, Is.EqualTo(core.SongNotes.Text));
            view.Undo();
            view.Sync();
            Assert.That(core.SongNotes.Text, Is.EqualTo(notes));

            // Typing: Enter writes "\r\n" in the box; the notes keep "\n" and the box follows.
            view.Editor.Text = notes + "\r\nTyped";
            Assert.That(core.SongNotes.Text, Is.EqualTo(notes + "\nTyped"));
            Assert.That(view.Editor.Text, Is.EqualTo(notes + "\nTyped"));
            SessionViewTests.Screenshot(view, "song-notes-wpf", 1100, 500);

            // A song with Buzz's info text and no notes: the text comes in as an imported note.
            core.SongNotes.Clear();
            core.InfoText = "Made with Buzz\r\ngreetings to the crew";
            string path = Path.Combine(Path.GetTempPath(), "neo-buzz-notes-" + Environment.ProcessId, "info-text.bmx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            (error, text, _) = rig.Call("song_save", new JsonObject { ["path"] = path });
            Assert.That(error, Is.False, text);
            (error, text, _) = rig.Call("song_open", new JsonObject { ["path"] = path, ["discard_unsaved"] = true });
            Assert.That(error, Is.False, text);
            view.Sync();
            Assert.That(view.Editor.Text, Is.EqualTo("Made with Buzz\ngreetings to the crew"));
            Assert.That(view.Banner.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(view.BannerText, Does.Contain("the song's info text"));
            Assert.That(core.InfoText, Is.EqualTo("Made with Buzz\r\ngreetings to the crew"), "left as it was");
            SessionViewTests.Screenshot(view, "song-notes-wpf-imported", 1100, 500);
        }

        [Test]
        public void TheInfoViewNoLongerBlanksTheInfoTextWhenItLoads()
        {
            // Its Loaded handler set the box to " " then "" through the TwoWay binding to InfoText.
            using var driver = new Driver();
            driver.Start();
            driver.Core.InfoText = "Made with Buzz";
            var view = new ReBuzz.InfoView(driver.Core);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.That(view.tbInfo.Text, Is.EqualTo("Made with Buzz"), "it shows the text");
            BindingOperations.GetBindingExpression(view.tbInfo, TextBox.TextProperty)?.UpdateSource();
            Assert.That(driver.Core.InfoText, Is.EqualTo("Made with Buzz"), "and writes nothing over it");
        }

        [Test]
        public void TheFileBrowsersDateColumnHoldsTheLongestDate()
        {
            // At 120 px "11/6/2026 9:17:27 AM" lost its last letter.
            var browser = new BuzzGUI.FileBrowser.FileBrowser();
            var list = (ListView)browser.FindName("listView");
            var column = ((GridView)list.View).Columns[2];
            string longest = new DateTime(2026, 12, 28, 22, 59, 59).ToString();
            var text = new FormattedText(longest, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(list.FontFamily, list.FontStyle, list.FontWeight, list.FontStretch), list.FontSize, Brushes.Black, 1.0);
            Assert.That(column.Width, Is.GreaterThanOrEqualTo(text.WidthIncludingTrailingWhitespace + 12), $"\"{longest}\" in its column");
        }
    }
}
