// Fork (docs/specs/ux-overhaul.md, the parameter panel redesign PP3): the polish after PP2's gallery. Docked cards that never clip under the
// sideways bar, a track table cut to the panel, floating cards as a masonry (each to the shortest column), and the keys toggle in the panel's
// bar instead of every knob's menu.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReBuzz.Core;
using ReBuzz.Studio.Views.Device;
using static ReBuzz.Studio.Tests.AudioHygieneTests;
using static ReBuzz.Studio.Tests.DeviceTestKit;
using Mode = ReBuzz.Studio.Views.Device.DeviceView.DeviceMode;

namespace ReBuzz.Studio.Tests
{
    public class UxPp3PanelTests
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

        static (Window Window, DevicePage Page, DeviceView View) Stage(MachineCore machine, bool floating, double width = 900, double height = 560)
        {
            var page = new DevicePage(machine, floating);
            page.SetEditor(false, remember: false);
            page.Parameters.SetMode(Mode.Knobs, remember: false);
            var window = new Window { Width = width, Height = height, Content = page };
            window.Show();
            MachineViewTests.Settle();
            Settle(window);
            return (window, page, page.Parameters);
        }

        static void Settle(Window w)
        {
            for (int i = 0; i < 3; i++)
            {
                Pump();
                w.UpdateLayout();
            }
        }

        static Point At(Avalonia.Controls.Control c, Avalonia.Controls.Control inside) => c.TranslatePoint(new Point(0, 0), inside)!.Value;

        [AvaloniaTest]
        public void DockedTheLastRowOfEveryCardIsInsideItAndTheBarNeverCoversACard()
        {
            foreach (var gear in new[] { "Dye Essence", "Neo Drums", "Neo EQ", "Neo Poly" })
            {
                var m = Add(gear, "PP3 Docked " + gear);
                foreach (double height in new[] { 460.0, 380.0, 330.0, 280.0 })
                {
                    var (window, page, view) = Stage(m, floating: false, width: 700, height: height);
                    try
                    {
                        var scroller = view.Cards.Scroller;
                        string where = $"{gear} at {height}";
                        double bottom = 0;
                        foreach (var (card, control) in view.Cards.Shown)
                        {
                            var at = At(control, view.Cards.Scroller);
                            bottom = Math.Max(bottom, at.Y + control.Bounds.Height + control.Margin.Bottom);
                            // the last value row sits inside the card's border
                            foreach (var p in card.Params)
                            {
                                var cell = view.Cards.Cell(p)!;
                                if (cell.Parent == null) continue;
                                Assert.That(At(cell, control).Y + cell.Bounds.Height, Is.LessThanOrEqualTo(control.Bounds.Height - 1), $"{where}: {card.Title} {p.Name} inside its card");
                            }
                        }
                        Assert.That(scroller.Extent.Width, Is.GreaterThan(scroller.Viewport.Width), $"{where}: it scrolls sideways at 700 pt");
                        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal && b.IsVisible);
                        Assert.That(bar, Is.Not.Null, where);
                        double barTop = bar!.TranslatePoint(new Point(0, 0), scroller)!.Value.Y;
                        Assert.That(bottom, Is.LessThanOrEqualTo(barTop + 0.5), $"{where}: the bar ({barTop:0}) is under the cards ({bottom:0})");
                        Assert.That(scroller.Extent.Height, Is.LessThanOrEqualTo(scroller.Viewport.Height + 1), $"{where}: nothing scrolls down");
                    }
                    finally { window.Close(); }
                }
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void DockedTheTrackTableCardIsCutToThePanelAndScrollsInside()
        {
            var m = Add("Keys Test", "PP3 Table");
            m.TrackCount = 3;
            var (window, page, view) = Stage(m, floating: false, width: 900, height: 260);
            try
            {
                var card = view.Cards.TableCard;
                Assert.That(card, Is.Not.Null);
                double room = view.Cards.Scroller.Viewport.Height;
                Assert.That(At(card!, view.Cards.Scroller).Y + card!.Bounds.Height + card.Margin.Bottom, Is.LessThanOrEqualTo(room + 0.5), "the table card fits the panel");
                Assert.That(view.Cards.Scroller.Extent.Height, Is.LessThanOrEqualTo(view.Cards.Scroller.Viewport.Height + 1), "the card row doesn't scroll down");
                Assert.That(view.Cards.Table.ContentHeight, Is.GreaterThan(0));
                var inner = card.GetVisualDescendants().OfType<ScrollViewer>().First();
                if (view.Cards.Table.ContentHeight > room)
                    Assert.That(inner.Extent.Height, Is.GreaterThan(inner.Viewport.Height), "what doesn't fit scrolls inside the card");
                window.Height = 700;
                Settle(window);
                Assert.That(card.Bounds.Height, Is.GreaterThan(60), "a taller panel shows the table whole again");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void FloatingCardsFlowInColumnsEachToTheShortestOneInReadingOrder()
        {
            var m = Add("Dye Essence", "PP3 Masonry");
            var (window, page, view) = Stage(m, floating: true, width: 1440, height: 900);
            try
            {
                foreach (double width in new[] { 1440.0, 1000.0, 640.0, 1440.0 })
                {
                    window.Width = width;
                    Settle(window);
                    var host = (CardFlow)view.Cards.Scroller.Content!;
                    var cards = view.Cards.Shown.Select(s => s.Control).ToList();
                    double room = view.Cards.Scroller.Viewport.Width;
                    string where = $"width {width}";
                    Assert.That(host.Columns.Count, Is.GreaterThanOrEqualTo(width >= 1000 ? 2 : 1), where);
                    foreach (var c in cards) Assert.That(At(c, host).X + c.Bounds.Width, Is.LessThanOrEqualTo(room + 1), $"{where}: inside the width");
                    Assert.That(cards.Max(c => At(c, host).X + c.Bounds.Width), Is.GreaterThanOrEqualTo(room - DeviceCards.CardChromeWidth), $"{where}: the columns fill the width");
                    // each card went to the shortest column (leftmost on a tie), in the cards' order
                    var bottoms = new double[host.Columns.Count];
                    var colOf = cards.Select(c => Math.Round(At(c, host).X)).Distinct().OrderBy(x => x).ToList();
                    Assert.That(colOf.Count, Is.EqualTo(host.Columns.Count), where);
                    bottoms = new double[colOf.Count];
                    for (int i = 0; i < cards.Count; i++)
                    {
                        var at = At(cards[i], host);
                        int col = colOf.IndexOf(Math.Round(at.X));
                        Assert.That(at.Y - cards[i].Margin.Top, Is.EqualTo(bottoms.Min()).Within(1), $"{where}: card {i} ({view.Cards.Shown[i].Card.Title}) starts at the shortest column's foot");
                        bottoms[col] = at.Y + cards[i].Bounds.Height + cards[i].Margin.Bottom;
                        if (i < colOf.Count) Assert.That(col, Is.EqualTo(i), $"{where}: the first row reads left to right");
                    }
                    double tallest = cards.Max(c => c.Bounds.Height);
                    Assert.That(bottoms.Max() - bottoms.Min(), Is.LessThanOrEqualTo(tallest + 8), $"{where}: the columns end level, to within one card");
                }
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void TheKeysToggleIsInThePanelBarAndThePresetsMenuAndNotInAKnobsMenu()
        {
            var m = Add("Neo Poly", "PP3 Keys");
            bool was = DeviceKeys.Enabled;
            using var restore = Studio.AfterTest(() => DeviceKeys.Enabled = was);
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                DeviceKeys.Enabled = false;
                var button = page.KeysButton;
                Assert.That(button.IsVisible, Is.True);
                Assert.That(button.Bounds.Right, Is.LessThanOrEqualTo(page.PresetsButton.Bounds.Left + 1), "next to Presets, in the bar");
                Assert.That(ToolTip.GetTip(button)?.ToString(), Does.Contain("Keyboard sends MIDI notes").And.Contain("off"));
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.That(DeviceKeys.Enabled, Is.True, "the button sets the registry setting");
                Assert.That(ToolTip.GetTip(button)?.ToString(), Does.Contain("on"));
                var presets = DeviceMenus.Presets(m, page, null);
                var item = presets.Items.OfType<MenuItem>().First(i => (string)i.Header! == "_Keyboard Sends MIDI Notes");
                Assert.That(item.IsChecked, Is.True, "the Presets menu shows the same state");
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Settle(window);
                Assert.That(DeviceKeys.Enabled, Is.False, "and sets it");
                Assert.That(ToolTip.GetTip(button)?.ToString(), Does.Contain("off"), "the bar follows the menu");
                var knob = DeviceMenus.ForParam(m, view.Cards.Shown.SelectMany(s => s.Card.Knobs).First(), null, null);
                Assert.That(knob.Items.OfType<MenuItem>().Select(i => (string)i.Header!), Does.Not.Contain("_Keyboard Sends MIDI Notes"));
            }
            finally { window.Close(); Remove(m); }
        }
    }
}
