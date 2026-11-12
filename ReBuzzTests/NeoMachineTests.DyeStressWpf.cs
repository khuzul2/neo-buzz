using AtmaFileSystem;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (docs/specs/dye-essence.md §5.5): the WPF hand check. Dye Essence's 704-parameter stress
    /// machine, and Dye Essence, in NeoBuzz Live's WPF parameter window (the theme's
    /// ParameterWindow.xaml with its view model, as the app opens it) and the WPF Modern Pattern Editor
    /// (the app's own build, pointed at a pattern of the machine): open, lay out, draw, reach the last
    /// parameter and column. Times are reported as notices. The asserts: the parameter window makes the
    /// rows in view only and opens in about a second, its last row moves its parameter and no other row
    /// moves one; the pattern editor draws a frame at 704 parameters in about the time of one at 106 (the
    /// columns in view only, the header's cells too), the keyboard reaches the last column, whose header
    /// is over it, and a header click moves the cursor.
    /// </summary>
    public partial class NeoMachineTests
    {
        static readonly NeoInfo DyeStress = new("Dye Stress", "Stress", true,
            builtPath: TestAssemblyDirectory.Value.AddDirectoryName("DyeStressGear").AddDirectoryName("Generators").AddFileName("Dye Stress.NET.dll").ToString());

        static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        static double Median(List<double> x)
        {
            x.Sort();
            return x[x.Count / 2];
        }

        /// <summary>An element's bounds in an ancestor's coordinates.</summary>
        static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        /// <summary>Layout done, and what it queued (a scroll viewer's commands, the rows' caches) run.</summary>
        static void Settle(Window window)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
        }

        /// <summary>The state parameters' values (every group but the inputs, every track).</summary>
        static List<(string name, int track, int value)> StateValues(MachineCore machine)
        {
            var values = new List<(string name, int track, int value)>();
            foreach (var g in machine.ParameterGroups.Where(g => g.Type != ParameterGroupType.Input))
                foreach (var p in g.Parameters.Where(p => p.Flags.HasFlag(ParameterFlags.State)))
                    for (int t = 0; t < Math.Max(1, g.TrackCount); t++)
                        values.Add((p.Name, t, p.GetValue(t)));
            return values;
        }

        sealed record WindowFigures(string Text, double OpenMs);

        /// <summary>
        /// The app's parameter window for a machine: open, laid out, paged through to the last row (no
        /// parameter moved by the rows made and recycled on the way), the last row's slider moved.
        /// </summary>
        static WindowFigures MeasureParameterWindow(MachineCore machine, string shot, int atLeast)
        {
            string xaml = Path.Combine(RepoRoot(), "RunTime", "Themes", "Default", "ParameterWindow.xaml");
            var before = StateValues(machine);
            var sw = Stopwatch.StartNew();
            var window = (BuzzGUI.ParameterWindow.ParameterWindow)BuzzGUI.Common.XamlReaderEx.LoadHack(xaml);
            var vm = new BuzzGUI.ParameterWindow.ParameterWindowVM { Machine = machine };
            window.DataContext = vm;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = 0;
            window.ShowActivated = false;
            window.Show();
            window.UpdateLayout();
            double openMs = sw.Elapsed.TotalMilliseconds;
            try
            {
                int rows = vm.Parameters.Count;
                Assert.That(rows, Is.GreaterThanOrEqualTo(atLeast), "a row per parameter");
                var list = Descendants<ItemsControl>(window).Single(i => ReferenceEquals(i.ItemsSource, vm.Parameters));
                var scroll = Descendants<ScrollViewer>(list).First();
                int made = Descendants<BuzzGUI.Common.ParameterSlider>(list).Count();
                // Virtualised: the rows in view are made (and a few more), not one per parameter.
                if (rows > 200) Assert.That(made, Is.LessThanOrEqualTo(250), $"rows made at opening, of {rows}");

                var frames = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    sw.Restart();
                    window.InvalidateVisual();
                    window.UpdateLayout();
                    frames.Add(sw.Elapsed.TotalMilliseconds);
                }

                // Every row in turn, a page at a time: the rows are made and recycled for other parameters.
                wpfStage += ", paging";
                sw.Restart();
                int pages = 0;
                for (double y = 0; y < scroll.ScrollableHeight; y += Math.Max(16, scroll.ViewportHeight))
                {
                    scroll.ScrollToVerticalOffset(y);
                    window.UpdateLayout();
                    pages++;
                }
                double pagesMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                scroll.ScrollToEnd();
                window.UpdateLayout();
                double scrollMs = sw.Elapsed.TotalMilliseconds;
                Settle(window);
                int madeAtEnd = Descendants<BuzzGUI.Common.ParameterSlider>(list).Count();
                var after = StateValues(machine);
                var moved = before.Zip(after, (b, a) => (b, a)).Where(x => x.b.value != x.a.value).Select(x => $"{x.b.name}[{x.b.track}] {x.b.value} -> {x.a.value}").ToList();
                Assert.That(moved, Is.Empty, "no parameter moved by opening the window and scrolling through every row");

                // The last row: made, in view, and its slider moves its parameter.
                var last = vm.Parameters.Last(p => p.Visibility == Visibility.Visible);
                var row = list.ItemContainerGenerator.ContainerFromItem(last) as FrameworkElement;
                Assert.That(row, Is.Not.Null, "the last row is made once scrolled to");
                var slider = Descendants<BuzzGUI.Common.ParameterSlider>(row!).Single();
                var box = BoundsIn(slider, scroll);
                Assert.That(box.Top, Is.GreaterThanOrEqualTo(-1.0), "the last row is in view");
                Assert.That(box.Bottom, Is.LessThanOrEqualTo(scroll.ViewportHeight + 1.0), "the last row is in view");
                int target = last.Value == last.MaxValue ? last.MinValue : last.MaxValue;
                slider.Value = target;
                Assert.That(last.Parameter.GetValue(0), Is.EqualTo(target), "the last row's slider moves its parameter");

                if (window.Content is FrameworkElement content)
                    SessionViewTests.Screenshot(content, shot, (int)Math.Max(200, content.ActualWidth), (int)Math.Max(150, Math.Min(4000, content.ActualHeight)));
                return new WindowFigures($"parameter window: {rows} rows ({vm.GlobalParameters.Count} global), {made} made; open and laid out in {openMs:F0} ms, " +
                       $"a layout pass {Median(frames):F1} ms (median of 5), {pages} pages through every row {pagesMs:F0} ms, to the last row {scrollMs:F0} ms " +
                       $"({madeAtEnd} rows made), no parameter moved; {window.ActualWidth:F0} x {window.ActualHeight:F0}", openMs);
            }
            finally
            {
                vm.Machine = null;
                window.Close();
            }
        }

        sealed record EditorFigures(string Text, double Frame, double Redraw, double RedrawAtLast);

        /// <summary>A key pressed in the pattern editor (as the keyboard sends it; no modifier is down).</summary>
        static void PressKey(UIElement target, Key key)
        {
            var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("the editor isn't shown");
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }

        /// <summary>A single left click on an element, as the mouse sends it.</summary>
        static void Click(UIElement target)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, 1);
            target.RaiseEvent(args);
        }

        /// <summary>How many colours a region of a frame has (a column drawn has its beats' and its text's).</summary>
        static int Colours(RenderTargetBitmap bitmap, Rect region)
        {
            int x = (int)Math.Max(0, Math.Floor(region.X)), y = (int)Math.Max(0, Math.Floor(region.Y));
            int w = (int)Math.Min(bitmap.PixelWidth - x, Math.Ceiling(region.Width)), h = (int)Math.Min(bitmap.PixelHeight - y, Math.Ceiling(region.Height));
            if (w <= 0 || h <= 0) return 0;
            var pixels = new int[w * h];
            bitmap.CopyPixels(new Int32Rect(x, y, w, h), pixels, w * 4, 0);
            return pixels.Distinct().Count();
        }

        /// <summary>
        /// The app's Modern Pattern Editor on a pattern of the machine: open, frames at the first and the last
        /// columns, the header over the columns in view, the keyboard to the last column, a header click.
        /// </summary>
        static EditorFigures MeasurePatternEditor(MachineCore machine, string shot, int atLeast)
        {
            var bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            var path = Path.Combine(bin, "Gear", "Generators", "Modern Pattern Editor.NET.dll");
            if (!File.Exists(path)) Assert.Inconclusive($"the Modern Pattern Editor isn't built ({path})");
            // The editor takes its look from the app's theme (Themes\Default\ModernPatternEditor\*.xaml beside
            // the app) and its keys from PEKeys.xml there (without it, it shows a message box and waits);
            // here the app is the tests' folder, so the repository's theme and keys go there first.
            string app = BuzzGUI.Common.Global.BuzzPath;
            string keys = Path.Combine(app, "PEKeys.xml");
            if (!File.Exists(keys)) File.Copy(Path.Combine(RepoRoot(), "RunTime", "PEKeys.xml"), keys);
            var theme = Path.Combine(RepoRoot(), "RunTime", "Themes", "Default");
            var themeHere = Path.Combine(app, "Themes", "Default");
            foreach (var file in Directory.GetFiles(theme, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(themeHere, Path.GetRelativePath(theme, file));
                if (File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
            }
            machine.CreatePattern("Stress", 16);
            var pattern = machine.Patterns.Last();

            var type = Assembly.LoadFrom(path).GetType("WDE.ModernPatternEditor.ModernPatternEditorMachine", true)!;
            var sw = Stopwatch.StartNew();
            wpfStage += ", created";
            var editor = Activator.CreateInstance(type, new object[] { null! })!;
            var control = (UserControl)type.GetMethod("PatternEditorControl")!.Invoke(editor, null)!;
            type.GetMethod("SetEditorPattern")!.Invoke(editor, new object[] { pattern });
            const int W = 1600, H = 1000;
            var window = new Window
            {
                Content = control,
                Width = W,
                Height = H,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = 0,
                ShowActivated = false,
            };
            wpfStage += ", showing";
            window.Show();
            window.UpdateLayout();
            double openMs = sw.Elapsed.TotalMilliseconds;
            wpfStage += ", shown";
            try
            {
                var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                // A frame as it is (composing what is drawn), and a full redraw (every element drawn again,
                // as after an edit to the machine's column set, whose beats the editor caches and redraws).
                double Frame(bool redraw)
                {
                    var t = Stopwatch.StartNew();
                    if (redraw)
                    {
                        control.InvalidateVisual();
                        foreach (var e in Descendants<FrameworkElement>(control)) e.InvalidateVisual();
                    }
                    window.UpdateLayout();
                    bitmap.Clear();
                    bitmap.Render(control);
                    return t.Elapsed.TotalMilliseconds;
                }
                var first = new List<double>();
                var plain = new List<double>();
                for (int i = 0; i < 7; i++) first.Add(Frame(true));
                for (int i = 0; i < 7; i++) plain.Add(Frame(false));
                // The grid's own scroll viewer (the header's scrolls with it).
                var scroll = Descendants<ScrollViewer>(control).FirstOrDefault(s => s.Name == "patternSV");
                var header = Descendants<ScrollViewer>(control).FirstOrDefault(s => s.Name == "headerSV");
                Assert.That(scroll, Is.Not.Null, "the grid scrolls");
                Assert.That(header, Is.Not.Null, "the header scrolls");
                if (atLeast > 200) Assert.That(scroll!.ExtentWidth, Is.GreaterThan(scroll.ViewportWidth), "more columns than fit: they scroll");
                // The header's cells (a label per column, a knob per state parameter) for the columns in view.
                List<Border> Labels() => Descendants<Border>(header!).Where(b => b.Height == 80 && b.Child is TextBlock).ToList();
                int labelsAtFirst = Labels().Count, knobsAtFirst = Descendants<BuzzGUI.Common.Knob>(header!).Count();
                if (atLeast > 200)
                    Assert.That(labelsAtFirst, Is.InRange(1, 250), "the header makes the labels of the columns in view, not of all");

                sw.Restart();
                scroll!.ScrollToHorizontalOffset(scroll.ScrollableWidth);
                window.UpdateLayout();
                Frame(false);
                double scrollMs = sw.Elapsed.TotalMilliseconds;
                Settle(window);
                double offset = scroll.HorizontalOffset;
                Assert.That(offset, Is.EqualTo(scroll.ScrollableWidth).Within(1.0), "the grid reaches its last column");
                Assert.That(header!.HorizontalOffset, Is.EqualTo(offset).Within(1.0), "the header follows the grid");
                var last = new List<double>();
                for (int i = 0; i < 7; i++) last.Add(Frame(true));
                int labelsAtLast = Labels().Count;
                if (atLeast > 200)
                    Assert.That(labelsAtLast, Is.InRange(1, 250), "the header makes the labels of the columns in view, not of all");

                // The keyboard: End takes the cursor to the set's last column (a machine's globals), in view,
                // drawn, its label over it; a click on another label takes the cursor there.
                string keyboard = "";
                var globals = machine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters;
                var patternControl = Descendants<UserControl>(control).FirstOrDefault(u => u.GetType().Name == "PatternControl");
                var cursor = Descendants<FrameworkElement>(control).FirstOrDefault(e => e.GetType().Name == "CursorElement");
                Assert.That(patternControl, Is.Not.Null, "the pattern control");
                Assert.That(cursor, Is.Not.Null, "the cursor");
                if (globals.Last().Type != ParameterType.Note)
                {
                    wpfStage += ", keyboard";
                    scroll.ScrollToHorizontalOffset(0);
                    Settle(window);
                    PressKey(patternControl!, Key.Home);
                    Settle(window);
                    PressKey(patternControl!, Key.End);
                    Settle(window);
                    var view = BoundsIn(scroll, control);
                    view.Width = scroll.ViewportWidth;
                    view.Height = scroll.ViewportHeight;
                    var at = BoundsIn(cursor!, control);
                    double cursorX = at.Left - view.Left + scroll.HorizontalOffset;
                    Assert.That(cursorX, Is.GreaterThan(scroll.ExtentWidth / 2), "End took the cursor to the last column of the machine's globals");
                    Assert.That(at.Left, Is.GreaterThanOrEqualTo(view.Left - 1), "the cursor is in view");
                    Assert.That(at.Right, Is.LessThanOrEqualTo(view.Right + 1), "the cursor is in view");
                    double centre = (at.Left + at.Right) / 2;
                    var labels = Labels().Select(b => (border: b, at: BoundsIn(b, control))).ToList();
                    var over = labels.Where(l => l.at.Left <= centre && centre < l.at.Right).ToList();
                    Assert.That(over, Has.Count.EqualTo(1), "the last column's label is made, over its column");
                    // The column left of the cursor's is drawn: its beats and text below the cursor's row.
                    bitmap.Clear();
                    bitmap.Render(control);
                    var below = new Rect(at.Left - at.Width - 2, at.Bottom + 2, at.Width, at.Height * 3);
                    int colours = Colours(bitmap, below);
                    Assert.That(colours, Is.GreaterThanOrEqualTo(3), "the columns by the last are drawn");
                    // A click on the label of the column left of it takes the cursor there.
                    var left = labels.Where(l => l.at.Right <= over[0].at.Left + 1).OrderByDescending(l => l.at.Left).FirstOrDefault();
                    Assert.That(left.border, Is.Not.Null, "the label left of the last");
                    Click(left.border);
                    Settle(window);
                    var moved = BoundsIn(cursor!, control);
                    var clicked = BoundsIn(left.border, control);
                    double movedCentre = (moved.Left + moved.Right) / 2;
                    Assert.That(movedCentre, Is.InRange(clicked.Left, clicked.Right), "a click on a column's label takes the cursor there");
                    keyboard = $"; End reached the last column (x {cursorX:F0} px) at offset {scroll.HorizontalOffset:F0} px, its label over it, {colours} colours drawn by it, a label click moved the cursor";
                }

                SessionViewTests.Screenshot(control, shot, W, H);
                return new EditorFigures($"pattern editor: opened in {openMs:F0} ms; a frame as drawn {Median(plain):F1} ms, a full redraw {Median(first):F1} ms at the first columns, " +
                       $"{Median(last):F1} ms at the last (medians of 7); to the last column {scrollMs:F0} ms (offset {offset:F0} of {scroll.ScrollableWidth:F0} px, extent {scroll.ExtentWidth:F0} px); " +
                       $"header cells made {labelsAtFirst} labels and {knobsAtFirst} knobs at the first columns, {labelsAtLast} labels at the last{keyboard}",
                       Median(plain), Median(first), Median(last));
            }
            finally
            {
                window.Content = null;
                window.Close();
                type.GetMethod("Release")?.Invoke(editor, null);
            }
        }

        static volatile string wpfStage = "start";

        [Test]
        public void DyeStressAndDyeEssenceInTheWpfParameterWindowAndPatternEditor()
        {
            // A modal dialog or a layout that never ends would hang the run silently: after three minutes
            // say where it stopped and end the test host.
            using var watchdog = new Timer(_ =>
            {
                Console.Error.WriteLine($"[DyeStress WPF] stuck at: {wpfStage}");
                Environment.FailFast($"DyeStressAndDyeEssenceInTheWpfParameterWindowAndPatternEditor stuck at: {wpfStage}");
            }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
            var windows = new List<WindowFigures>();
            var editors = new List<EditorFigures>();
            foreach (var (info, atLeast, name) in new[] { (DyeStress, 704, "dye-stress"), (Dye, 97, "dye-essence") })
            {
                using var rig = new Rig(info);
                var machine = rig.Effect;
                int globals = machine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Count;
                Assert.That(globals, Is.GreaterThanOrEqualTo(atLeast), info.Name);
                wpfStage = info.Name + ": parameter window";
                var window = MeasureParameterWindow(machine, name + "-wpf-parameters", globals);
                windows.Add(window);
                TestContext.Out.WriteLine($"[notice] {info.Name} in WPF: {window.Text}");
                wpfStage = info.Name + ": pattern editor";
                var editor = MeasurePatternEditor(machine, name + "-wpf-pattern-editor", globals);
                editors.Add(editor);
                TestContext.Out.WriteLine($"[notice] {info.Name} in WPF: {editor.Text}");
            }

            // dye-essence.md §5.5: at 704 parameters the window opens in about a second (2.5-3.7 s when it
            // made every row), and a frame of the pattern editor costs about what one at 106 does with the
            // same pixels on screen (2.3 times when it drew every column). Bounds with room for the runners.
            var (stress, essence) = (editors[0], editors[1]);
            double frameRatio = stress.Frame / essence.Frame, redrawRatio = stress.Redraw / essence.Redraw, lastRatio = stress.RedrawAtLast / essence.RedrawAtLast;
            TestContext.Out.WriteLine($"[notice] Dye Stress against Dye Essence in WPF: the parameter window opened in {windows[0].OpenMs:F0} ms against {windows[1].OpenMs:F0} ms; " +
                                      $"a pattern editor frame {frameRatio:F2}x, a full redraw {redrawRatio:F2}x at the first columns and {lastRatio:F2}x at the last");
            Assert.That(windows[0].OpenMs, Is.LessThan(2000), "Dye Stress's parameter window opens in about a second");
            Assert.That(frameRatio, Is.LessThan(1.6), "a frame at 704 parameters against one at 106");
            Assert.That(redrawRatio, Is.LessThan(1.6), "a full redraw at 704 parameters against one at 106 (first columns)");
            Assert.That(lastRatio, Is.LessThan(1.6), "a full redraw at 704 parameters against one at 106 (last columns)");
        }
    }
}
