using BuzzGUI.Interfaces;
using ReBuzz.Common;
using ReBuzzTests.Automation;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
