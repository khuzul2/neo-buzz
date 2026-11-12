// Fork (docs/specs/ux-overhaul.md P3a, second step: decisions 31-34): the browser on the left (the machines to add, the
// patterns of the selection, a search box), the detail panel under the view (the Tracker, not a tab any more) and the
// Tracker maximised over the window (F2, ⇧⌘F). Closed or open, sized and remembered like the inspector.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3a2Tests
    {
        sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Drums, Bass;
            public PatternCore Beat, Line;
            public StudioSelection Selection => StudioSelection.For(Song.Core);
            public BrowserPanel Browser => Window.Browser;
            public DetailPanel Detail => Window.Detail;

            public Rig(double width = 1280, double height = 800)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Drums", "Bass");
                Drums = Song.Machine("Drums");
                Bass = Song.Machine("Bass");
                Beat = Song.Pattern(Drums, "Beat", 16, (0, 60), (4, 62));
                Line = Song.Pattern(Bass, "Line", 16, (0, 48));
                Song.Layout.SetSlot(0, 0, Beat);
                Song.Layout.SetSlot(1, 0, Line);
                Selection.Clear(this);
                Window = new MainWindow(Studio.Host) { Width = width, Height = height };
                Window.Show();
                Show("Machines");
            }

            public void Show(string tab)
            {
                Window.ShowView(tab);
                Settle();
            }

            public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.KeyPressQwerty(key, mods);
                Window.KeyReleaseQwerty(key, mods);
                Settle();
            }

            /// <summary>⌘ on a Mac, Ctrl elsewhere, plus ⌥ (Alt).</summary>
            public static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

            public ListBoxItem Row(string tag) => Browser.List.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag! == tag);

            public Point CentreOf(Avalonia.Controls.Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

            public void Click(Button button) => Song.Click(button);

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        /// <summary>The settings these tests change, put back as found (the registry is the whole run's).</summary>
        static void PutBack()
        {
            var registry = Studio.Host.Registry;
            registry.Write(BrowserPanel.OpenKey, false, BrowserPanel.RegistrySection);
            registry.Write(BrowserPanel.WidthKey, BrowserPanel.DefaultWidth, BrowserPanel.RegistrySection);
            registry.Write(DetailPanel.OpenKey, false, DetailPanel.RegistrySection);
            registry.Write(DetailPanel.HeightKey, DetailPanel.DefaultHeight, DetailPanel.RegistrySection);
        }

        // ------------------------------------------------------------------ keys

        [AvaloniaTest]
        public void TheKeysArePlatformCorrect()
        {
            // ⌥⌘B, ⌥⌘D and ⇧⌘F on a Mac; Ctrl+Alt+B, Ctrl+Alt+D and Ctrl+Shift+F elsewhere (as the inspector's ⌥⌘I).
            var browser = Shell.Commands.StudioKeymap.Get("view.browser");
            var detail = Shell.Commands.StudioKeymap.Get("view.detail");
            var maximise = Shell.Commands.StudioKeymap.Get("view.maximise");
            Assert.That(browser.Gesture(true), Is.EqualTo(new KeyGesture(Key.B, KeyModifiers.Meta | KeyModifiers.Alt)));
            Assert.That(browser.Gesture(false), Is.EqualTo(new KeyGesture(Key.B, KeyModifiers.Control | KeyModifiers.Alt)));
            Assert.That(detail.Gesture(true), Is.EqualTo(new KeyGesture(Key.D, KeyModifiers.Meta | KeyModifiers.Alt)));
            Assert.That(detail.Gesture(false), Is.EqualTo(new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Alt)));
            Assert.That(maximise.Gesture(true), Is.EqualTo(new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Shift)));
            Assert.That(maximise.Gestures(false).Select(g => g.Key), Is.EqualTo(new[] { Key.F, Key.F2 }), "⇧⌘F, and F2 as the old Pattern tab's key");
            Assert.That(maximise.Gesture(false).KeyModifiers, Is.EqualTo(KeyModifiers.Control | KeyModifiers.Shift));
            Assert.That(Shell.Commands.StudioKeymap.Get("view.pattern").Gesture(true), Is.EqualTo(new KeyGesture(Key.D4, KeyModifiers.Meta)), "⌘4 is still the fourth view");
        }

        [AvaloniaTest]
        public void TheBrowserAndTheDetailAreClosedAtFirstAndTheirKeysToggleThem()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            Assert.That(rig.Browser.IsOpen, Is.False);
            Assert.That(rig.Browser.Bounds.Width, Is.EqualTo(BrowserPanel.RailWidth).Within(1), "a rail");
            Assert.That(rig.Detail.IsOpen, Is.False);
            Assert.That(rig.Detail.Bounds.Height, Is.EqualTo(DetailPanel.BarHeight).Within(1), "a bar");
            Assert.That(rig.Detail.Page.IsEffectivelyVisible, Is.False, "the Tracker isn't shown");

            var mods = Rig.Cmd | RawInputModifiers.Alt;
            foreach (string view in new[] { "Machines", "Arrangement", "Session" })
            {
                rig.Show(view);
                rig.Key(PhysicalKey.B, mods);
                Assert.That(rig.Browser.IsOpen, Is.True, $"{view}: ⌥⌘B opens the browser");
                Assert.That(rig.Browser.Bounds.Width, Is.EqualTo(BrowserPanel.DefaultWidth).Within(1));
                rig.Key(PhysicalKey.D, mods);
                Assert.That(rig.Detail.IsOpen, Is.True, $"{view}: ⌥⌘D opens the detail");
                Assert.That(rig.Detail.Bounds.Height, Is.EqualTo(DetailPanel.DefaultHeight).Within(1));
                rig.Key(PhysicalKey.B, mods);
                rig.Key(PhysicalKey.D, mods);
                Assert.That(rig.Browser.IsOpen || rig.Detail.IsOpen, Is.False, $"{view}: and again closes them");
            }

            var view2 = AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().ToList();
            Assert.That(view2.Select(e => e.Text), Does.Contain("Browser").And.Contain("Detail").And.Contain("Maximise Tracker"));
            AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().Single(e => e.Text == "Browser").Run();
            Settle();
            Assert.That(rig.Browser.IsOpen, Is.True, "the menu's entry");
            Assert.That(AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().Single(e => e.Text == "Browser").IsChecked(), Is.True);
        }

        // ------------------------------------------------------------------ the browser

        [AvaloniaTest]
        public void TheBrowserListsTheMachinesAndThePatternsOfTheSelection()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Browser.Open();
            Settle();
            var rows = rig.Browser.Rows;
            Assert.That(rows, Does.Contain("#Synths").And.Contain("#EQ & Filter").And.Contain("gear:Neo Poly").And.Contain("gear:Neo EQ"));
            Assert.That(rows.Any(r => r.StartsWith("#Patterns of")), Is.False, "no patterns while nothing is selected");

            rig.Selection.Set(rig.Drums, null, this);
            Settle();
            Assert.That(rig.Browser.Rows, Does.Contain("#Patterns of Drums").And.Contain("pattern:Beat"));
            Assert.That(rig.Browser.Rows, Does.Not.Contain("pattern:Line"), "only the selected machine's");

            rig.Selection.Set(rig.Bass, rig.Line, this);
            Settle();
            Assert.That(rig.Browser.Rows, Does.Contain("pattern:Line"));
            Assert.That(((ListBoxItem)rig.Browser.List.SelectedItem!).Tag, Is.EqualTo("pattern:Line"), "the selected pattern is marked");
            rig.Selection.Clear(this);
            Settle();
            Assert.That(rig.Browser.List.SelectedItem, Is.Null);
        }

        [AvaloniaTest]
        public void TheSearchBoxNarrowsTheList()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Browser.Open();
            rig.Selection.Set(rig.Bass, null, this);
            Settle();
            rig.Browser.Search.Text = "poly";
            Settle();
            Assert.That(rig.Browser.Rows, Does.Contain("gear:Neo Poly").And.Not.Contain("gear:Neo EQ"), "a part of the name, any case");
            Assert.That(rig.Browser.Rows.First(r => r.StartsWith("gear:")), Is.EqualTo("gear:Neo Poly"), "the name's match is the best (P3d: tags and use_for match too, below it)");
            rig.Browser.Search.Text = "line";
            Settle();
            Assert.That(rig.Browser.Rows, Does.Contain("pattern:Line"), "patterns too");
            rig.Browser.Search.Text = "qqqqzzzz";
            Settle();
            Assert.That(rig.Browser.Rows.Where(r => !r.StartsWith("none:") && !r.StartsWith("#Patterns")), Is.Empty);
            Assert.That(rig.Browser.Rows.Count(r => r.StartsWith("none:")), Is.EqualTo(0).Or.EqualTo(1));
            rig.Browser.Search.Text = "";
            Settle();
            Assert.That(rig.Browser.Rows, Does.Contain("gear:Neo EQ"), "an empty box shows everything again");
        }

        [AvaloniaTest]
        public void AClickOnAPatternSelectsItEverywhereAndADoubleClickOpensTheTracker()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Browser.Open();
            rig.Selection.Set(rig.Drums, null, this);
            Settle();
            var row = rig.Row("pattern:Beat");
            var p = rig.CentreOf(row);
            rig.Window.MouseDown(p, MouseButton.Left);
            rig.Window.MouseUp(p, MouseButton.Left);
            Settle();
            Assert.That(rig.Selection.Machine, Is.SameAs(rig.Drums));
            Assert.That(rig.Selection.Pattern, Is.SameAs(rig.Beat), "the studio's selection: the inspector and the other views follow");
            Assert.That(rig.Window.Inspector.Facts.Single(f => f.Label == "Pattern").Value, Is.EqualTo("Beat"));
            Assert.That(rig.Detail.IsOpen, Is.False, "one click only selects");

            DoubleClick(rig.Window, rig.CentreOf(rig.Row("pattern:Beat")));
            Settle();
            Assert.That(rig.Detail.IsOpen, Is.True, "a double-click opens it in the Tracker");
            Assert.That(PatternView.Page!.Pattern, Is.SameAs(rig.Beat));
        }

        [AvaloniaTest]
        public void ADoubleClickOnAMachineAddsItInOneUndoStepAndSelectsIt()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Browser.Open();
            Settle();
            int before = rig.Song.Core.Song.Machines.Count;
            rig.Browser.Search.Text = "neo eq";        // the list is long now (categories, tags): the row is at the top
            Settle();
            DoubleClick(rig.Window, rig.CentreOf(rig.Row("gear:Neo EQ")));
            Settle();
            Assert.That(rig.Song.Core.Song.Machines.Count, Is.EqualTo(before + 1));
            Assert.That(rig.Selection.Machine?.DLL.Name, Is.EqualTo("Neo EQ"), "the new machine is the selection");
            rig.Song.Core.SongCore.ActionStack.Undo();
            Settle();
            Assert.That(rig.Song.Core.Song.Machines.Count, Is.EqualTo(before), "one undo takes it away");
        }

        [AvaloniaTest]
        public void TheBrowserKeepsItsWidthAndStateAndIsSizedByItsEdge()
        {
            using var restore = Studio.AfterTest(PutBack);
            var registry = Studio.Host.Registry;
            using (var rig = new Rig())
            {
                rig.Browser.Open();
                rig.Browser.PanelWidth = 10;
                Assert.That(rig.Browser.PanelWidth, Is.EqualTo(BrowserPanel.MinPanelWidth));
                rig.Browser.PanelWidth = 4000;
                Assert.That(rig.Browser.PanelWidth, Is.EqualTo(BrowserPanel.MaxPanelWidth));
                rig.Browser.PanelWidth = BrowserPanel.DefaultWidth;
                Settle();
                var start = rig.Browser.Grip.TranslatePoint(new Point(2, 200), rig.Window)!.Value;
                rig.Window.MouseDown(start, MouseButton.Left);
                rig.Window.MouseMove(start + new Vector(40, 0), RawInputModifiers.LeftMouseButton);
                rig.Window.MouseMove(start + new Vector(80, 0), RawInputModifiers.LeftMouseButton);
                rig.Window.MouseUp(start + new Vector(80, 0), MouseButton.Left);
                Settle();
                Assert.That(rig.Browser.PanelWidth, Is.EqualTo(BrowserPanel.DefaultWidth + 80).Within(8), "80 points right is 80 wider");
            }
            Assert.That(registry.Read(BrowserPanel.OpenKey, false, BrowserPanel.RegistrySection), Is.True);
            using (var rig = new Rig())
            {
                Assert.That(rig.Browser.IsOpen, Is.True, "the next window has it open");
                Assert.That(rig.Browser.PanelWidth, Is.GreaterThan(BrowserPanel.DefaultWidth + 60), "and as wide");
                rig.Click(rig.Browser.CollapseButton);
                Assert.That(rig.Browser.IsOpen, Is.False);
            }
            using (var rig = new Rig())
                Assert.That(rig.Browser.IsOpen, Is.False, "closed stays closed");
        }

        // ------------------------------------------------------------------ the detail panel and the Tracker

        [AvaloniaTest]
        public void ThePatternViewIsTheDetailPanelsTrackerNotATab()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            Assert.That(rig.Window.Tabs.Items.Cast<TabItem>().Select(t => t.Header), Is.EqualTo(new[] { "Arrangement", "Session", "Machines" }));
            Assert.That(rig.Detail.Page, Is.SameAs(PatternView.Page));
            rig.Key(PhysicalKey.Digit4, Rig.Cmd);
            Assert.That(rig.Detail.IsOpen, Is.True, "⌘4: the Tracker");
            Assert.That(rig.Detail.IsMaximised, Is.False, "docked");
            Assert.That(rig.Detail.Page.IsEffectivelyVisible, Is.True);
            Assert.That(rig.Window.Tabs.IsEffectivelyVisible, Is.True, "the view stays above it");
            Assert.That(rig.Window.Tabs.Bounds.Height, Is.GreaterThan(150));
            Assert.That(((TabItem)rig.Window.Tabs.SelectedItem!).Header, Is.EqualTo("Machines"), "the view behind is as it was");
            var item = AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().Single(e => e.Text == "Tracker");
            Assert.That(item.IsChecked(), Is.True);

            // The other views and the inspector open it in the Tracker too (their own button or key), the song's pattern first.
            rig.Detail.Close();
            Settle();
            rig.Selection.Set(rig.Drums, rig.Beat, this);
            rig.Window.ShowView("Pattern");
            Settle();
            Assert.That(PatternView.Page!.Pattern, Is.SameAs(rig.Beat), "it shows the selected pattern");
        }

        [AvaloniaTest]
        public void F2AndShiftCommandFMaximiseTheTrackerAndTheViewsKeysBringTheViewsBack()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Key(PhysicalKey.F2);
            Assert.That(rig.Detail.IsMaximised, Is.True, "F2");
            Assert.That(rig.Window.Tabs.IsEffectivelyVisible, Is.False, "the view is behind it");
            Assert.That(rig.Detail.Bounds.Height, Is.GreaterThan(500), "over the whole window");
            Assert.That(rig.Detail.MaximiseButton.Content, Is.EqualTo("⤡"));
            rig.Key(PhysicalKey.F2);
            Assert.That(rig.Detail.IsOpen, Is.False, "F2 again: back to the bar it came from");
            Assert.That(rig.Window.Tabs.IsEffectivelyVisible, Is.True);

            rig.Key(PhysicalKey.Digit4, Rig.Cmd);          // docked first
            rig.Key(PhysicalKey.F, Rig.Cmd | RawInputModifiers.Shift);
            Assert.That(rig.Detail.IsMaximised, Is.True, "⇧⌘F");
            rig.Key(PhysicalKey.F, Rig.Cmd | RawInputModifiers.Shift);
            Assert.That(rig.Detail.IsMaximised, Is.False);
            Assert.That(rig.Detail.IsOpen, Is.True, "back to the docked panel it came from");

            rig.Click(rig.Detail.MaximiseButton);
            Assert.That(rig.Detail.IsMaximised, Is.True, "the button");
            rig.Key(PhysicalKey.F4);                 // ⌘1–⌘9 are the focused Tracker's own (the beat's subdivision): the F-keys are the views'
            Assert.That(rig.Detail.IsMaximised, Is.False, "F4: the arrangement is back");
            Assert.That(((TabItem)rig.Window.Tabs.SelectedItem!).Header, Is.EqualTo("Arrangement"));
            Assert.That(rig.Window.Tabs.IsEffectivelyVisible, Is.True);
        }

        [AvaloniaTest]
        public void TheDetailKeepsItsHeightAndIsSizedByItsEdge()
        {
            using var restore = Studio.AfterTest(PutBack);
            var registry = Studio.Host.Registry;
            using (var rig = new Rig())
            {
                rig.Detail.Open();
                Settle();
                rig.Detail.PanelHeight = 10;
                Assert.That(rig.Detail.PanelHeight, Is.EqualTo(DetailPanel.MinPanelHeight));
                rig.Detail.PanelHeight = 99999;
                Assert.That(rig.Detail.PanelHeight, Is.EqualTo(DetailPanel.MaxPanelHeight));
                Settle();
                Assert.That(rig.Window.Tabs.Bounds.Height + rig.Window.Switcher.Bounds.Height, Is.GreaterThanOrEqualTo(MainWindow.MinMainHeight - 1), "the view keeps its room");
                rig.Detail.PanelHeight = 300;
                Settle();
                var start = rig.Detail.Grip.TranslatePoint(new Point(300, 2), rig.Window)!.Value;
                rig.Window.MouseDown(start, MouseButton.Left);
                rig.Window.MouseMove(start - new Vector(0, 40), RawInputModifiers.LeftMouseButton);
                rig.Window.MouseMove(start - new Vector(0, 70), RawInputModifiers.LeftMouseButton);
                rig.Window.MouseUp(start - new Vector(0, 70), MouseButton.Left);
                Settle();
                Assert.That(rig.Detail.PanelHeight, Is.EqualTo(370).Within(8), "70 points up is 70 taller");
            }
            Assert.That(registry.Read(DetailPanel.OpenKey, false, DetailPanel.RegistrySection), Is.True);
            using (var rig = new Rig())
            {
                Assert.That(rig.Detail.IsOpen, Is.True, "the next window has it open, as tall");
                Assert.That(rig.Detail.PanelHeight, Is.GreaterThan(350));
                Assert.That(rig.Detail.IsMaximised, Is.False, "never maximised at first");
                rig.Click(rig.Detail.CollapseButton);
                Assert.That(rig.Detail.IsOpen, Is.False);
            }
        }

        [AvaloniaTest]
        public void TheEditCommandsGoToTheTrackerWhileItHasTheKeyboard()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Window.ShowView("Pattern");
            Settle();
            var inside = rig.Detail.Page.GetVisualDescendants().OfType<InputElement>().First(c => c.Focusable && c.IsEffectivelyVisible);
            inside.Focus();
            Settle();
            var target = typeof(MainWindow).GetProperty("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.That(target.GetValue(rig.Window), Is.SameAs(rig.Detail.Page), "the Tracker's edit commands");
            ((MachineView)rig.Window.Tabs.SelectedContent!).Canvas.Focus();
            Settle();
            Assert.That(target.GetValue(rig.Window), Is.SameAs(rig.Window.Tabs.SelectedContent), "and the view's when the keyboard is there");
        }

        // ------------------------------------------------------------------ room

        [AvaloniaTest]
        public void ANarrowAndALowWindowKeepTheViewUsable()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig(640, 400);
            rig.Browser.Open();
            rig.Window.Inspector.Open();
            rig.Detail.Open();
            Settle();
            Assert.That(rig.Window.Tabs.Bounds.Width, Is.GreaterThanOrEqualTo(MainWindow.MinMainWidth - 2), "the panels give way to the view");
            Assert.That(rig.Window.Tabs.Bounds.Height + rig.Window.Switcher.Bounds.Height, Is.GreaterThanOrEqualTo(MainWindow.MinMainHeight - 2), "the switcher is part of the view's room");
            var inspector = rig.Window.Inspector;
            Assert.That(inspector.IsSqueezed, Is.False, "the inspector keeps what it needs to show its header and buttons");
            Assert.That(inspector.Bounds.Width, Is.InRange(Views.Inspector.InspectorPanel.MinPanelWidth - 1, Views.Inspector.InspectorPanel.DefaultWidth + 1));
            Assert.That(rig.Browser.IsOpen, Is.True, "the setting is as it was");
            Assert.That(rig.Browser.IsSqueezed, Is.True, "but with no room it is a rail");
            Assert.That(rig.Browser.Bounds.Width, Is.EqualTo(BrowserPanel.RailWidth).Within(1));
            Assert.That(rig.Browser.RailButton.IsEffectivelyVisible, Is.True, "with its button");
            Assert.That(rig.Browser.PanelWidth, Is.EqualTo(BrowserPanel.DefaultWidth), "the fit doesn't change the width setting");

            rig.Window.Width = 900;
            Settle();
            Assert.That(rig.Browser.IsSqueezed, Is.False, "900 wide has room for both");
            Assert.That(rig.Browser.Bounds.Width, Is.EqualTo(BrowserPanel.DefaultWidth).Within(1));
            Assert.That(inspector.Bounds.Width, Is.EqualTo(Views.Inspector.InspectorPanel.DefaultWidth).Within(1));
            Assert.That(rig.Window.Tabs.Bounds.Width, Is.GreaterThanOrEqualTo(MainWindow.MinMainWidth - 2));

            rig.Window.Width = 1280;
            rig.Window.Height = 800;
            Settle();
            Assert.That(rig.Browser.Bounds.Width, Is.EqualTo(BrowserPanel.DefaultWidth).Within(1), "wide again: as set");
            Assert.That(inspector.Bounds.Width, Is.EqualTo(Views.Inspector.InspectorPanel.DefaultWidth).Within(1));
        }

        [AvaloniaTest]
        public void TheTextMeetsTheContrastFloorOnTheTokens()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var surface = StudioTokens.SurfacePanel;
            Assert.That(((Avalonia.Media.ISolidColorBrush)rig.Browser.Background!).Color, Is.EqualTo(surface));
            Assert.That(((Avalonia.Media.ISolidColorBrush)rig.Detail.Background!).Color, Is.EqualTo(surface));
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, surface), Is.GreaterThanOrEqualTo(4.5), "machine and pattern names");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, surface), Is.GreaterThanOrEqualTo(4.5), "the group headers, the closed Tracker button");
            var fill = StudioTokens.SelectionFill;
            double a = fill.A / 255.0;
            var open = Avalonia.Media.Color.FromRgb((byte)(fill.R * a + surface.R * (1 - a)), (byte)(fill.G * a + surface.G * (1 - a)), (byte)(fill.B * a + surface.B * (1 - a)));
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, open), Is.GreaterThanOrEqualTo(4.5), "the open Tracker button, the selected row");
            // The controls really use them.
            rig.Browser.Open();
            rig.Detail.Open();
            Settle();
            Assert.That(((Avalonia.Media.ISolidColorBrush)rig.Detail.TrackerButton.Foreground!).Color, Is.EqualTo(StudioTokens.TextPrimary));
            var header = rig.Browser.List.Items.OfType<ListBoxItem>().First(i => ((string)i.Tag!).StartsWith("#"));
            Assert.That(((Avalonia.Media.ISolidColorBrush)((TextBlock)header.Content!).Foreground!).Color, Is.EqualTo(StudioTokens.TextSecondary));
        }
    }
}
