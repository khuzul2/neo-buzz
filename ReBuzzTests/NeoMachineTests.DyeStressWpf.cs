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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (docs/specs/dye-essence.md §5.5): the WPF hand check. Dye Essence's 704-parameter stress
    /// machine, and Dye Essence, in NeoBuzz Live's WPF parameter window (the theme's
    /// ParameterWindow.xaml with its view model, as the app opens it) and the WPF Modern Pattern Editor
    /// (the app's own build, pointed at a pattern of the machine): open, lay out, draw, reach the last
    /// parameter and column. Times are reported as notices; the asserts are bounds, the targets are
    /// the spec's (the window in about a second, a frame of the grid well under 100 ms).
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

        /// <summary>The app's parameter window for a machine: open, laid out, scrolled to the last row, the last row moved.</summary>
        static string MeasureParameterWindow(MachineCore machine, string shot, int atLeast)
        {
            string xaml = Path.Combine(RepoRoot(), "RunTime", "Themes", "Default", "ParameterWindow.xaml");
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
                var scroll = Descendants<ScrollViewer>(window).OrderByDescending(s => s.ExtentHeight).First();
                var frames = new List<double>();
                for (int i = 0; i < 5; i++)
                {
                    sw.Restart();
                    window.InvalidateVisual();
                    window.UpdateLayout();
                    frames.Add(sw.Elapsed.TotalMilliseconds);
                }
                sw.Restart();
                scroll.ScrollToEnd();
                window.UpdateLayout();
                double scrollMs = sw.Elapsed.TotalMilliseconds;
                // The last global row moves its parameter.
                var last = vm.GlobalParameters.Last();
                int target = last.Value == last.MaxValue ? last.MinValue : last.MaxValue;
                last.Value = target;
                Assert.That(last.Parameter.GetValue(0), Is.EqualTo(target), "the last row moves its parameter");
                if (window.Content is FrameworkElement content)
                    SessionViewTests.Screenshot(content, shot, (int)Math.Max(200, content.ActualWidth), (int)Math.Max(150, Math.Min(4000, content.ActualHeight)));
                return $"parameter window: {rows} rows ({vm.GlobalParameters.Count} global), open and laid out in {openMs:F0} ms, " +
                       $"a layout pass {Median(frames):F1} ms (median of 5), to the last row {scrollMs:F0} ms, {window.ActualWidth:F0} x {window.ActualHeight:F0}";
            }
            finally
            {
                vm.Machine = null;
                window.Close();
            }
        }

        /// <summary>The app's Modern Pattern Editor on a pattern of the machine: open, frames at the first and the last columns.</summary>
        static string MeasurePatternEditor(MachineCore machine, string shot, int atLeast)
        {
            var bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            var path = Path.Combine(bin, "Gear", "Generators", "Modern Pattern Editor.NET.dll");
            if (!File.Exists(path)) Assert.Inconclusive($"the Modern Pattern Editor isn't built ({path})");
            // The editor takes its look from the app's theme (Themes\Default\ModernPatternEditor\*.xaml beside
            // the app); here the app is the tests' folder, so the repository's theme goes there first.
            var theme = Path.Combine(RepoRoot(), "RunTime", "Themes", "Default");
            var themeHere = Path.Combine(TestAssemblyDirectory.Value.ToString(), "Themes", "Default");
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
            window.Show();
            window.UpdateLayout();
            double openMs = sw.Elapsed.TotalMilliseconds;
            try
            {
                var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                double Frame()
                {
                    var t = Stopwatch.StartNew();
                    control.InvalidateVisual();
                    foreach (var e in Descendants<FrameworkElement>(control)) e.InvalidateVisual();
                    window.UpdateLayout();
                    bitmap.Clear();
                    bitmap.Render(control);
                    return t.Elapsed.TotalMilliseconds;
                }
                var first = new List<double>();
                for (int i = 0; i < 7; i++) first.Add(Frame());
                var scroll = Descendants<ScrollViewer>(control).OrderByDescending(s => s.ExtentWidth).FirstOrDefault();
                Assert.That(scroll, Is.Not.Null, "the grid scrolls");
                if (atLeast > 200) Assert.That(scroll!.ExtentWidth, Is.GreaterThan(scroll.ViewportWidth), "more columns than fit: they scroll");
                sw.Restart();
                scroll!.ScrollToRightEnd();
                window.UpdateLayout();
                double scrollMs = sw.Elapsed.TotalMilliseconds;
                var last = new List<double>();
                for (int i = 0; i < 7; i++) last.Add(Frame());
                SessionViewTests.Screenshot(control, shot, W, H);
                return $"pattern editor: {pattern.Columns.Count} columns, opened in {openMs:F0} ms; a frame {Median(first):F1} ms at the first columns, " +
                       $"{Median(last):F1} ms at the last (median of 7); to the last column {scrollMs:F0} ms (extent {scroll.ExtentWidth:F0} px)";
            }
            finally
            {
                window.Content = null;
                window.Close();
                type.GetMethod("Release")?.Invoke(editor, null);
            }
        }

        [Test]
        public void DyeStressAndDyeEssenceInTheWpfParameterWindowAndPatternEditor()
        {
            var lines = new List<string>();
            foreach (var (info, globals, name) in new[] { (DyeStress, 704, "dye-stress"), (Dye, 97, "dye-essence") })
            {
                using var rig = new Rig(info);
                var machine = rig.Effect;
                Assert.That(machine.ParameterGroups.Single(g => g.Type == ParameterGroupType.Global).Parameters.Count, Is.EqualTo(globals), info.Name);
                string window = MeasureParameterWindow(machine, name + "-wpf-parameters", globals);
                TestContext.Out.WriteLine($"[notice] {info.Name} in WPF: {window}");
                string editor = MeasurePatternEditor(machine, name + "-wpf-pattern-editor", globals);
                lines.Add($"{info.Name}: {window}; {editor}");
                TestContext.Out.WriteLine($"[notice] {info.Name} in WPF: {editor}");
            }
            Assert.That(lines, Has.Count.EqualTo(2));
        }
    }
}
