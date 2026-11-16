// Fork (docs/specs/ux-overhaul.md, the parameter panel redesign PP1, quick fixes): Dye Essence's doubled unit, Neo Plug's hidden unbound
// slots, a slider track at 3:1, tabular values, a name column that fits its names, the Device page bar on two lines when narrow, and
// a macro strip that wraps instead of dropping cells.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Device;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxPp1QuickFixTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string gear, string name)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(machine, Is.Not.Null, $"{gear} loads");
            return machine!;
        }

        static void Remove(MachineCore machine)
        {
            Core.UiHost.ReleaseMachine(machine);
            Core.SongCore.DeleteMachines(new[] { machine });
            Dispatcher.UIThread.RunJobs();
        }

        static (Window Window, DevicePage Page, DeviceView View) Stage(MachineCore machine, double width, bool floating = false, double height = 640)
        {
            var page = new DevicePage(machine, floating);
            page.SetEditor(false, remember: false);
            var window = new Window { Width = width, Height = height, Content = page };
            window.Show();
            Settle();
            window.UpdateLayout();
            return (window, page, page.Parameters);
        }

        [AvaloniaTest]
        public void ADegreeUnitIsNotShownTwice()
        {
            var m = Add("Dye Essence", "PP1 Degrees");
            var (window, _, view) = Stage(m, 720);
            try
            {
                var phase = view.Layout.All.Where(p => p.Unit == "°" && p.Kind == DeviceKind.Slider).ToList();
                Assert.That(phase, Is.Not.Empty, "Dye Essence has parameters in degrees");
                foreach (var p in phase.Take(20))
                    foreach (int raw in new[] { p.Min, p.Default, p.Max })
                    {
                        string text = p.Describe(raw);
                        Assert.That(text.Count(c => c == '°'), Is.LessThanOrEqualTo(1), $"{p.FullName} at {raw}: \"{text}\"");
                    }
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void NeoPlugWithNothingLoadedHidesItsSlotsAndSaysToLoadAPlugin()
        {
            var m = Add("Neo Plug", "PP1 Plug");
            var (window, _, view) = Stage(m, 720);
            try
            {
                Assert.That(view.Layout.All.Count, Is.LessThan(8), "the unbound slots are not rows");
                Assert.That(view.Layout.All.Any(p => p.FullName.StartsWith("Slot ")), Is.False);
                Assert.That(view.Layout.NothingLoaded, Is.True, "nothing is loaded in it");
                Assert.That(view.NothingLoadedShown, Is.True);
                Assert.That(DeviceView.NothingLoadedText, Is.EqualTo("Load a plug-in"));
                Assert.That(view.Scroller.IsVisible, Is.False);
                Assert.That(DeviceLayout.HiddenCount(m), Is.GreaterThan(0), "the slots exist but are hidden");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheSliderTrackIsThreeToOneInBothThemes()
        {
            var kind = StudioTheme.Kind;
            using var restore = Studio.AfterTest(() => { StudioTheme.Kind = kind; StudioTheme.Refresh(); });
            foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light })
            {
                StudioTheme.Kind = theme;
                StudioTheme.Refresh();
                double ratio = StudioTokens.Contrast(StudioTokens.ControlTrack, StudioTokens.SurfacePanel);
                Assert.That(ratio, Is.GreaterThanOrEqualTo(3.0), $"{theme}: the track against the panel");
            }
        }

        [AvaloniaTest]
        public void TheNameColumnFitsTheLongestNameUpTo160AndNamesHaveTooltips()
        {
            var m = Add("Neo Poly", "PP1 Names");
            var (window, _, view) = Stage(m, 720);
            try
            {
                Assert.That(view.List.NameColumn, Is.InRange(56, DeviceList.MaxNameColumn));
                var longest = view.Layout.All.OrderByDescending(p => p.Name.Length).First();
                var shortest = view.Layout.All.Where(p => !p.IsTrack).OrderBy(p => p.Name.Length).First();
                Assert.That(longest.Name.Length, Is.GreaterThan(shortest.Name.Length));
                Assert.That(DeviceList.NameTip(longest), Does.StartWith(longest.FullName));
                var cutoff = view.Layout.All.First(p => p.FullName == "F1 cutoff");
                Assert.That(DeviceList.NameTip(cutoff), Does.Contain("F1 cutoff"));
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheDeviceBarWrapsToTwoLinesUnder520AndStaysOneLineAbove()
        {
            var m = Add("Neo Poly", "PP1 Bar");
            try
            {
                var (narrow, page, view) = Stage(m, 420, floating: true);
                try
                {
                    Assert.That(page.BarWrapped, Is.True, "420 pt: two lines");
                    var title = page.Bar.GetVisualDescendants().OfType<TextBlock>().First();
                    var tool = view.Toolbar.TranslatePoint(new Point(0, 0), page.Bar)!.Value;
                    var ttl = title.TranslatePoint(new Point(0, 0), page.Bar)!.Value;
                    Assert.That(tool.Y, Is.GreaterThanOrEqualTo(ttl.Y + title.Bounds.Height - 1), "the search is on the second line, under the name");
                    Assert.That(page.FloatButton.TranslatePoint(new Point(0, 0), page.Bar)!.Value.Y, Is.LessThan(tool.Y), "the buttons stay on the first");
                    Assert.That(view.Toolbar.Bounds.Right, Is.LessThanOrEqualTo(page.Bar.Bounds.Width + 1), "the search stays inside");
                }
                finally { narrow.Close(); }
                var (wide, page2, view2) = Stage(m, 700, floating: true);
                try
                {
                    Assert.That(page2.BarWrapped, Is.False);
                    Assert.That(page2.Bar.Bounds.Height, Is.LessThanOrEqualTo(32.5), "one 32 pt line");
                }
                finally { wide.Close(); }
            }
            finally { Remove(m); }
        }

        [AvaloniaTest]
        public void TheMacroStripWrapsInsteadOfDroppingCells()
        {
            var m = Add("Neo Poly", "PP1 Macros");
            var (window, page, view) = Stage(m, 300, floating: true);
            try
            {
                var strip = view.MacroStrip;
                var item = (MacroItem)strip.Items.Single();
                Assert.That(item.Cells.Count, Is.GreaterThan(2));
                Assert.That(item.PerRow, Is.LessThan(item.Cells.Count), "300 pt cannot hold every cell in a row");
                Assert.That(item.Rows, Is.GreaterThan(1));
                Assert.That(strip.Bounds.Height, Is.EqualTo(item.Rows * MacroItem.StripHeight).Within(1), "the strip is as tall as its rows");
                foreach (var p in item.Cells)
                {
                    Assert.That(strip.TryGetBounds(p, out var cell), Is.True, p.FullName);
                    Assert.That(cell.Right, Is.LessThanOrEqualTo(strip.Bounds.Width + 1), p.FullName + " is inside the strip");
                    Assert.That(cell.Bottom, Is.LessThanOrEqualTo(strip.Bounds.Height + 1), p.FullName);
                    var hit = strip.HitTest(new Point(cell.Center.X, cell.Bottom - 8));
                    Assert.That(hit.Param, Is.SameAs(p), "every cell can be reached");
                }
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheDockedPagePutsMacrosAndSearchInOneBar()
        {
            var m = Add("Neo Poly", "PP1 Dock");
            var (window, page, view) = Stage(m, 1400);
            try
            {
                Assert.That(page.BarMacros, Is.Not.Null, "docked and wide: the macros are in the bar");
                Assert.That(page.Bar.Bounds.Height, Is.LessThanOrEqualTo(32.5), "one 32 pt bar");
                var macro = page.BarMacros!.TranslatePoint(new Point(0, 0), page.Bar)!.Value;
                var search = view.Toolbar.TranslatePoint(new Point(0, 0), page.Bar)!.Value;
                Assert.That(Math.Abs(macro.Y - search.Y), Is.LessThan(16), "on the same row as the search");
                Assert.That(page.BarMacros.Bounds.Width, Is.GreaterThan(400));
                var item = (MacroItem)page.BarMacros.Items.Single();
                Assert.That(item.Compact && item.Rows == 1, Is.True);
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }
    }
}
